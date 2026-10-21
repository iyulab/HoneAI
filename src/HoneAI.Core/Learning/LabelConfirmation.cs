using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Trust;

namespace HoneAI.Learning;

/// <summary>
/// The trust a confirming judge gets while the teacher ledger has not measured it — an order of the kinds,
/// not a measurement. Consumer-set; the defaults put a person above a language or vision model above the
/// rest.
/// </summary>
public sealed record LabelTrustDefaults
{
    /// <summary>The defaults: <see cref="JudgeKind.Human"/> 1, <see cref="JudgeKind.Llm"/> and <see cref="JudgeKind.Vlm"/> 0.8, any other kind 0.7.</summary>
    public static LabelTrustDefaults Standard { get; } = new();

    /// <summary>The trust of each kind, in (0, 1].</summary>
    public IReadOnlyDictionary<JudgeKind, double> ByKind { get; init; } = new Dictionary<JudgeKind, double>
    {
        [JudgeKind.Human] = 1.0,
        [JudgeKind.Llm] = 0.8,
        [JudgeKind.Vlm] = 0.8,
    };

    /// <summary>The trust of a kind not in <see cref="ByKind"/>.</summary>
    public double Otherwise { get; init; } = 0.7;

    /// <summary>The default trust of <paramref name="kind"/>.</summary>
    public double Of(JudgeKind kind) => ByKind.TryGetValue(kind, out var trust) ? trust : Otherwise;
}

/// <summary>
/// Decides whether answers from above a learning tier make a label for it, and with what weight — the one
/// place the rules of the learning loop live: only a tier above may confirm; a settled decision is a label
/// only when it was drawn for recheck, at its draw's π; judges above that disagree on a must-catch value
/// wait for consensus; trust is the ledger's measurement, else the kind's default, said so.
/// </summary>
public static class LabelConfirmation
{
    /// <summary>
    /// The confirmation of <paramref name="decisionId"/> for <paramref name="learner"/>.
    /// </summary>
    /// <param name="decisionId">The decision.</param>
    /// <param name="decision">The provenance the decision left HoneAI with — the tier that settled it and its recheck draw.</param>
    /// <param name="answers">What tiers and people above answered, each with the provenance of the tier that answered.</param>
    /// <param name="learner">The tier the label is for. An answer from it, or from below it, confirms nothing.</param>
    /// <param name="tiers">The ladder — where each answering tier sits.</param>
    /// <param name="policy">Which answers agree, and which are must-catch.</param>
    /// <param name="teacherCell">The teacher ledger's cell for a tier, or <see langword="null"/> when it has none.</param>
    /// <param name="confirmedAt">When the label is confirmed.</param>
    /// <param name="defaults">Trust for an unmeasured judge; <see cref="LabelTrustDefaults.Standard"/> when null.</param>
    /// <param name="annotations">
    /// The references the sinks need, carried onto the label — for MLoop, the prediction id of the ML tier's
    /// answer under <see cref="MLoopFeedbackSink{TValue}.PredictionIdAnnotation"/>.
    /// </param>
    public static Confirmation<TValue> Decide<TValue>(
        string decisionId,
        PredictionProvenance decision,
        IReadOnlyList<ITracedPrediction<TValue>> answers,
        Tier learner,
        IReadOnlyCollection<Tier> tiers,
        ISettlementPolicy<TValue> policy,
        Func<string, TeacherCell?> teacherCell,
        DateTimeOffset confirmedAt,
        LabelTrustDefaults? defaults = null,
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(learner);
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(teacherCell);
        defaults ??= LabelTrustDefaults.Standard;

        // Only a tier above the learner confirms: a judge agreeing with itself is not evidence.
        var above = answers
            .Select(a => (Answer: a, Tier: tiers.FirstOrDefault(t => t.Id == a.Provenance.TierId)))
            .Where(x => x.Tier is not null && x.Tier.CostRank > learner.CostRank)
            .Select(x => (x.Answer, Tier: x.Tier!))
            .ToList();
        if (above.Count == 0)
            return new(ConfirmationOutcome.NotALabel, Reason: $"No answer from a tier above '{learner.Id}'.");

        // π: a decision that went up to the confirming tier reached it with certainty; one a lower tier settled
        // reached it only if drawn for recheck — and then with its draw's probability.
        var settledBelow = decision.TierId is { } settledBy
            && tiers.FirstOrDefault(t => t.Id == settledBy) is { } settler
            && above.All(x => x.Tier.CostRank > settler.CostRank);
        double probability = 1.0;
        if (settledBelow)
        {
            if (decision.Sample is not { Drawn: true } draw)
                return new(ConfirmationOutcome.NotALabel,
                    Reason: $"Settled by '{decision.TierId}' and not drawn for recheck: no tier above saw it.");
            probability = draw.Probability;
        }

        // Judges above that disagree where one says must-catch: neither wins until a consensus judgment.
        var values = above.Select(x => x.Answer.Value).ToList();
        var disagree = values.Any(v => values.Any(w => !policy.Agrees(v, w)));
        if (disagree && values.Any(policy.MustCatch))
            return new(ConfirmationOutcome.NeedsConsensus,
                Reason: "Judges above disagree on a must-catch value; it waits for a consensus judgment.");

        // The most trusted confirmer's answer stands (on a tie, the one higher in the cost order).
        var scored = above
            .Select(x => (x.Answer, x.Tier, Trust: TrustOf(x.Tier, teacherCell, defaults)))
            .OrderByDescending(x => x.Trust.Value)
            .ThenByDescending(x => x.Tier.CostRank)
            .First();

        return new(ConfirmationOutcome.Confirmed, new ConfirmedLabel<TValue>
        {
            DecisionId = decisionId,
            SliceKey = decision.SliceKey,
            Value = scored.Answer.Value,
            ConfirmedBy = scored.Tier.Id,
            Kind = scored.Tier.Kind,
            ConfirmedByVersion = scored.Answer.Provenance.TierVersion ?? scored.Tier.Version,
            SnapshotVersion = decision.SnapshotVersion,
            Probability = probability,
            Trust = scored.Trust.Value,
            TrustBasis = scored.Trust.Basis,
            ConfirmedAt = confirmedAt,
            Annotations = annotations,
        });
    }

    private static (double Value, LabelTrustBasis Basis) TrustOf(
        Tier tier, Func<string, TeacherCell?> teacherCell, LabelTrustDefaults defaults) =>
        teacherCell(tier.Id) is { Plants: > 0, Accuracy: { } accuracy }
            ? (accuracy, LabelTrustBasis.Measured)
            : (defaults.Of(tier.Kind), LabelTrustBasis.Default);
}
