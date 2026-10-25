using System;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// Where a pooled answer goes: one judgment becomes the response the pool records, the recheck result every
/// measurement takes (a plant's record, a repeat, a double check), and — once its window is accounted — the sample
/// draw a label is confirmed with.
/// </summary>
public static class PoolAnswers
{
    /// <summary>
    /// The response to record for <paramref name="verdict"/> on <paramref name="shown"/>: answered or passed, and for a
    /// recheck, whether it agrees with <paramref name="settled"/> and is a must-catch value.
    /// </summary>
    /// <param name="shown">What was shown.</param>
    /// <param name="checkedBy">The tier that judged — the rung of people.</param>
    /// <param name="reviewerKey">Who judged — a pseudonymous key.</param>
    /// <param name="verdict">The judgment; an abstention is a pass.</param>
    /// <param name="settled">The settled answer, for a recheck — the consumer's, never shown to the judge.</param>
    /// <param name="policy">Which answers agree, and which are must-catch.</param>
    /// <param name="at">When.</param>
    public static PoolResponse Response<TValue>(
        PoolPresentation shown, string checkedBy, string reviewerKey, JudgeVerdict<TValue> verdict, TValue settled,
        ISettlementPolicy<TValue> policy, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerKey);
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(policy);
        var response = new PoolResponse
        {
            Purpose = shown.Purpose,
            DecisionId = shown.DecisionId,
            TierId = shown.TierId,
            CheckedBy = checkedBy,
            ReviewerKey = reviewerKey,
            RespondedAt = at,
            Kind = verdict.Answered ? PoolResponseKind.Answered : PoolResponseKind.Skipped,
        };
        if (!verdict.Answered || shown.Purpose != PoolPurpose.Recheck)
            return response;
        return response with
        {
            Outcome = policy.Agrees(settled, verdict.Value!) ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed,
            UpperMustCatch = policy.MustCatch(verdict.Value!),
        };
    }

    /// <summary>
    /// The judgment as a recheck result — blind, as everything the pool shows is — for <c>PlantRecords.From</c>,
    /// <see cref="RepeatConsistency.Pair{TValue}"/> and <see cref="DoubleCheckMeasure.Pair{TValue}"/>.
    /// </summary>
    public static RecheckResult<TValue> Result<TValue>(PoolPresentation shown, Tier checker, JudgeVerdict<TValue> verdict, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(verdict);
        return new RecheckResult<TValue>(shown.DecisionId, checker, verdict, at);
    }

    /// <summary>
    /// The draw a pooled decision reached its judge with — π × r once its window is accounted — to put on the
    /// decision's provenance when confirming a label from the answer (<c>LabelConfirmation.Decide</c>), so the label
    /// is weighted as the ledger weighs the recheck.
    /// </summary>
    /// <exception cref="ArgumentException">The reach is of another tier or slice, or nothing was reached.</exception>
    public static SampleDraw ReachedDraw(PoolItem item, PoolReach reach)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(reach);
        if (reach.TierId != item.TierId || reach.SliceKey != item.SliceKey)
            throw new ArgumentException($"The reach is of {reach.TierId} in '{reach.SliceKey}', the item of {item.TierId} in '{item.SliceKey}'.", nameof(reach));
        if (reach.Reached == 0)
            throw new ArgumentException("Nothing in the window was reached; the item cannot have been.", nameof(reach));
        return new SampleDraw(item.Probability * reach.Rate, item.Knob ?? "pool", Drawn: true) { KnobRates = item.KnobRates };
    }
}
