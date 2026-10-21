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

    /// <summary>
    /// The trust of a consensus (<see cref="LabelConfirmation.Settle{TValue}"/>), in (0, 1] — 1 by default: a
    /// consensus is the known answer teachers are measured against, so it is not counted below them.
    /// </summary>
    public double Consensus { get; init; } = 1.0;

    /// <summary>The default trust of <paramref name="kind"/>.</summary>
    public double Of(JudgeKind kind) => ByKind.TryGetValue(kind, out var trust) ? trust : Otherwise;
}

/// <summary>
/// Decides whether answers from above a learning tier make a label for it, and with what weight — the one
/// place the rules of the learning loop live: only a tier above may confirm; a settled decision is a label
/// only when it was drawn for recheck, at its draw's π; judges above that disagree on a must-catch value
/// wait for consensus (<see cref="Settle{TValue}"/>); trust is the ledger's measurement, else the kind's default, said so.
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
    /// <param name="teacherCell">
    /// The teacher ledger's cell for a judge — a tier, or a person within a rung of people
    /// (<see cref="TeacherLedger.Compute"/> with <c>judgedBy</c>) — or <see langword="null"/> when it has none.
    /// </param>
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
        Func<JudgeIdentity, TeacherCell?> teacherCell,
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

        if (ReachProbability(decision, above.Select(x => x.Tier), tiers) is not { } probability)
            return new(ConfirmationOutcome.NotALabel,
                Reason: $"Settled by '{decision.TierId}' and not drawn for recheck: no tier above saw it.");

        // Judges above that disagree where one says must-catch: neither wins until a consensus judgment.
        var values = above.Select(x => x.Answer.Value).ToList();
        var disagree = values.Any(v => values.Any(w => !policy.Agrees(v, w)));
        if (disagree && values.Any(policy.MustCatch))
            return new(ConfirmationOutcome.NeedsConsensus,
                Reason: "Judges above disagree on a must-catch value; it waits for a consensus judgment.");

        // The most trusted confirmer's answer stands (on a tie, the one higher in the cost order).
        var scored = above
            .Select(x => (x.Answer, x.Tier, Trust: TrustOf(x.Tier, x.Answer.Provenance.JudgedBy, teacherCell, defaults)))
            .OrderByDescending(x => x.Trust.Value)
            .ThenByDescending(x => x.Tier.CostRank)
            .First();

        return new(ConfirmationOutcome.Confirmed, new ConfirmedLabel<TValue>
        {
            DecisionId = decisionId,
            SliceKey = decision.SliceKey,
            Value = scored.Answer.Value,
            ConfirmedBy = scored.Tier.Id,
            JudgedBy = scored.Answer.Provenance.JudgedBy,
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

    /// <summary>
    /// The label a consensus makes for <paramref name="learner"/>: the value <paramref name="consensus"/> settled
    /// on, trusted as <see cref="LabelTrustDefaults.Consensus"/> (<see cref="LabelTrustBasis.Consensus"/>), at
    /// the π the decision reached the participants with — the same rules as <see cref="Decide{TValue}"/>.
    /// </summary>
    /// <param name="decision">The provenance the decision left HoneAI with.</param>
    /// <param name="consensus">The consensus. Its participants are two or more distinct judges of tiers on the ladder.</param>
    /// <param name="learner">The tier the label is for. Every participant sits above it.</param>
    /// <param name="tiers">The ladder.</param>
    /// <param name="defaults">The consensus trust; <see cref="LabelTrustDefaults.Standard"/> when null.</param>
    /// <param name="annotations">The references the sinks need, as in <see cref="Decide{TValue}"/>.</param>
    /// <exception cref="ArgumentException">
    /// The consensus is not one: fewer than two distinct participants, a participant whose tier is not on the
    /// ladder, or one at or below the learner — a judge agreeing with itself is not evidence, and neither is the learner.
    /// </exception>
    public static Confirmation<TValue> Settle<TValue>(
        PredictionProvenance decision,
        ConsensusJudgment<TValue> consensus,
        Tier learner,
        IReadOnlyCollection<Tier> tiers,
        LabelTrustDefaults? defaults = null,
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(consensus);
        ArgumentNullException.ThrowIfNull(learner);
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentException.ThrowIfNullOrWhiteSpace(consensus.DecisionId, nameof(consensus));
        defaults ??= LabelTrustDefaults.Standard;

        var participants = new List<(JudgeIdentity Judge, Tier Tier)>();
        foreach (var judge in consensus.Participants?.Distinct() ?? [])
        {
            ArgumentNullException.ThrowIfNull(judge, nameof(consensus));
            var tier = tiers.FirstOrDefault(t => t.Id == judge.TierId)
                ?? throw new ArgumentException($"Consensus participant '{judge}' is not on a tier of the ladder.", nameof(consensus));
            if (tier.CostRank <= learner.CostRank)
                throw new ArgumentException(
                    $"Consensus participant '{judge}' does not sit above '{learner.Id}': a tier cannot confirm its own label.",
                    nameof(consensus));
            participants.Add((judge, tier));
        }
        if (participants.Count < 2)
            throw new ArgumentException(
                $"A consensus takes two or more distinct judges; decision '{consensus.DecisionId}' names {participants.Count}.",
                nameof(consensus));

        if (ReachProbability(decision, participants.Select(p => p.Tier), tiers) is not { } probability)
            return new(ConfirmationOutcome.NotALabel,
                Reason: $"Settled by '{decision.TierId}' and not drawn for recheck: no tier above saw it.");

        var ordered = participants
            .OrderBy(p => p.Tier.CostRank)
            .ThenBy(p => p.Judge.ToString(), StringComparer.Ordinal)
            .ToList();
        return new(ConfirmationOutcome.Confirmed, new ConfirmedLabel<TValue>
        {
            DecisionId = consensus.DecisionId,
            SliceKey = decision.SliceKey,
            Value = consensus.Value,
            ConfirmedBy = string.Join('+', ordered.Select(p => p.Judge)),
            Participants = ordered.Select(p => p.Judge).ToList(),
            Kind = ordered[^1].Tier.Kind,
            SnapshotVersion = decision.SnapshotVersion,
            Probability = probability,
            Trust = defaults.Consensus,
            TrustBasis = LabelTrustBasis.Consensus,
            ConfirmedAt = consensus.ReachedAt,
            Annotations = annotations,
        });
    }

    // π: a decision that went up to the confirming tiers reached them with certainty; one a lower tier settled
    // reached them only if drawn for recheck — and then with its draw's probability. Null: settled, never drawn.
    private static double? ReachProbability(
        PredictionProvenance decision, IEnumerable<Tier> confirmers, IReadOnlyCollection<Tier> tiers)
    {
        var settledBelow = decision.TierId is { } settledBy
            && tiers.FirstOrDefault(t => t.Id == settledBy) is { } settler
            && confirmers.All(t => t.CostRank > settler.CostRank);
        if (!settledBelow)
            return 1.0;
        return decision.Sample is { Drawn: true } draw ? draw.Probability : null;
    }

    private static (double Value, LabelTrustBasis Basis) TrustOf(
        Tier tier, string? judgedBy, Func<JudgeIdentity, TeacherCell?> teacherCell, LabelTrustDefaults defaults) =>
        teacherCell(new JudgeIdentity(tier.Id, judgedBy)) is { Plants: > 0, Accuracy: { } accuracy }
            ? (accuracy, LabelTrustBasis.Measured)
            : (defaults.Of(tier.Kind), LabelTrustBasis.Default);
}
