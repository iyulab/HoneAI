using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// The rules an <see cref="EvidencePromotionGate"/> judges by — registered before the candidate is measured, so the
/// evidence cannot choose its own bar.
/// </summary>
public sealed record EvidenceCriteria
{
    /// <summary>
    /// The held-out labels the candidate and production are scored on — a file in the MLoop project, built from the
    /// labels of <c>LabelShare.Evaluation</c> (never from what the candidate trained on).
    /// </summary>
    public required string EvaluationDataPath { get; init; }

    /// <summary>The file's weight column — each held-out label's 1/(π · q).</summary>
    public string? Weight { get; init; }

    /// <summary>
    /// How much worse than production the candidate may be and still be called no worse: the upper end of the paired
    /// interval for candidate − production loss must not exceed it. 0: no worse at all.
    /// </summary>
    public double Margin { get; init; }

    /// <summary>Fewest rows the comparison may rest on.</summary>
    public int MinimumRows { get; init; } = 30;

    /// <summary>
    /// The must-catch classes. When given, the candidate's recall of them on the held-out labels must not fall more
    /// than <see cref="MustCatchRecallMargin"/> under production's — a model as accurate overall that catches fewer of
    /// these is not promoted.
    /// </summary>
    public IReadOnlyCollection<string>? MustCatch { get; init; }

    /// <summary>How far under production's must-catch recall the candidate's may be.</summary>
    public double MustCatchRecallMargin { get; init; }
}

/// <summary>
/// Evidence decides: the candidate and the model in production are scored on the same held-out labels, paired per
/// row, and the candidate is promoted when it is shown no worse (<see cref="EvidenceCriteria"/>). It never waits — the
/// labels it was given are all it will see, so evidence too thin to show the candidate no worse refuses it, and
/// production stays. The first model of a name, with nothing in production to compare against, is promoted.
/// </summary>
/// <remarks>
/// A promotion it makes is a tier's new version shown no worse (<see cref="PromotionVerdict.ProvenNoWorse"/>) — it
/// keeps the old version's standing in the trust lifecycle rather than earning it in shadow from the start.
/// </remarks>
public sealed class EvidencePromotionGate : IPromotionGate
{
    private readonly IMLoopClient _mloop;
    private readonly EvidenceCriteria _criteria;

    /// <param name="mloop">The MLoop the candidate was trained in.</param>
    /// <param name="criteria">The rules.</param>
    public EvidencePromotionGate(IMLoopClient mloop, EvidenceCriteria criteria)
    {
        _mloop = mloop ?? throw new ArgumentNullException(nameof(mloop));
        _criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        ArgumentException.ThrowIfNullOrWhiteSpace(criteria.EvaluationDataPath, nameof(criteria));
        if (!(criteria.Margin >= 0) || !(criteria.MustCatchRecallMargin >= 0))
            throw new ArgumentOutOfRangeException(nameof(criteria), "Margins are not negative.");
        if (criteria.MinimumRows < 1)
            throw new ArgumentOutOfRangeException(nameof(criteria), criteria.MinimumRows, "At least one row.");
    }

    /// <inheritdoc />
    public async Task<PromotionVerdict> JudgeAsync(PromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (await _mloop.GetInfoAsync(candidate.Model, cancellationToken).ConfigureAwait(false) is null)
            return Decided(PromotionDecision.Promote, "nothing is in production to compare with — the first model", provenNoWorse: false);

        var evaluation = await _mloop.EvaluateAsync(new MLoopEvaluationRequest(
            _criteria.EvaluationDataPath, candidate.ExperimentId, candidate.Model, _criteria.Weight, Against: "production"),
            cancellationToken).ConfigureAwait(false);
        if (evaluation.Paired is not { } paired)
            return Refused($"the candidate and production could not be compared row by row: {evaluation.NotPaired ?? "no comparison"}");
        if (paired.Rows < _criteria.MinimumRows)
            return Refused($"{paired.Rows} held-out rows, under the {_criteria.MinimumRows} the comparison needs");

        var interval = $"{paired.Measure} candidate − production {F(paired.Difference)} [{F(paired.Low)}, {F(paired.High)}] on {paired.Rows} rows";
        if (paired.High > _criteria.Margin)
            return Refused($"not shown no worse: {interval}, upper end over the margin {F(_criteria.Margin)}");

        var reason = $"shown no worse: {interval}, within the margin {F(_criteria.Margin)}";
        if (_criteria.MustCatch is { Count: > 0 } mustCatch)
        {
            var production = await _mloop.EvaluateAsync(new MLoopEvaluationRequest(
                _criteria.EvaluationDataPath, ExperimentId: evaluation.Against, Model: candidate.Model, Weight: _criteria.Weight),
                cancellationToken).ConfigureAwait(false);
            var candidateRecall = evaluation.Confusion?.Recall(mustCatch);
            var productionRecall = production.Confusion?.Recall(mustCatch);
            if (candidateRecall is not { } c || productionRecall is not { } p)
                return Refused($"no held-out row is labeled {string.Join(" / ", mustCatch)}, so must-catch recall cannot be judged");
            if (c < p - _criteria.MustCatchRecallMargin)
                return Refused($"must-catch recall {F(c)} under production's {F(p)} by more than {F(_criteria.MustCatchRecallMargin)}");
            reason += $"; must-catch recall {F(c)} against production's {F(p)}";
        }
        return Decided(PromotionDecision.Promote, reason, provenNoWorse: true);
    }

    private static PromotionVerdict Refused(string reason) => Decided(PromotionDecision.Refuse, reason, provenNoWorse: false);

    private static PromotionVerdict Decided(PromotionDecision decision, string reason, bool provenNoWorse)
        => new(decision, reason, JudgeKind.Statistics) { ProvenNoWorse = provenNoWorse };

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
