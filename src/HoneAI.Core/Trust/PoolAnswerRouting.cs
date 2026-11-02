using System;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>What a judge did with a pooled item: it answered, passed, or could not tell.</summary>
/// <typeparam name="TValue">The answer type.</typeparam>
public readonly record struct PoolJudgment<TValue>
{
    /// <summary>Answered, passed (<see cref="PoolResponseKind.Skipped"/>) or could not tell (<see cref="PoolResponseKind.Unsure"/>).</summary>
    public PoolResponseKind Kind { get; private init; }

    /// <summary>The answer, when the judge answered.</summary>
    public TValue? Value { get; private init; }

    /// <summary>Why the judge passed, when it said.</summary>
    public string? Reason { get; private init; }

    /// <summary>The judge answered.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static PoolJudgment<TValue> Answer(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new() { Kind = PoolResponseKind.Answered, Value = value };
    }

    /// <summary>
    /// The judge saw the item and passed — informative, as an upper tier's failure is: hard inputs are passed more often.
    /// </summary>
    public static PoolJudgment<TValue> Pass(string? reason = null) => new() { Kind = PoolResponseKind.Skipped, Reason = reason };

    /// <summary>
    /// The judge saw the item and could not tell — not a pass, not a label, and not the upper tier's failure.
    /// </summary>
    public static PoolJudgment<TValue> CouldNotTell() => new() { Kind = PoolResponseKind.Unsure };
}

/// <summary>
/// What the consumer holds that an answer is judged against — which depends on why the item was shown
/// (<see cref="PoolPurpose"/>). The judge never sees any of it.
/// </summary>
/// <typeparam name="TValue">The answer type.</typeparam>
public sealed record PoolAnswerBasis<TValue>
{
    private PoolAnswerBasis()
    {
    }

    /// <summary>The purpose this basis is for; <see cref="PoolAnswerRouting.Route{TValue}"/> refuses a different one.</summary>
    public PoolPurpose Purpose { get; private init; }

    /// <summary>For a recheck: the answer the tier settled the decision with.</summary>
    public TValue? Settled { get; private init; }

    /// <summary>For a planted item: its known answer.</summary>
    public TValue? Truth { get; private init; }

    /// <summary>For a planted item: where the known answer comes from, when worth recording.</summary>
    public string? TruthSource { get; private init; }

    /// <summary>For a repeat or a second opinion: the earlier blind judgment of the same input.</summary>
    public RecheckResult<TValue>? Earlier { get; private init; }

    /// <summary>For a repeat or a second opinion: the known answer, when there is one, so both judgments are scored against it.</summary>
    public Known<TValue>? Known { get; private init; }

    /// <summary>A recheck of a decision the tier settled with <paramref name="settled"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="settled"/> is null.</exception>
    public static PoolAnswerBasis<TValue> ForRecheck(TValue settled)
    {
        ArgumentNullException.ThrowIfNull(settled);
        return new() { Purpose = PoolPurpose.Recheck, Settled = settled };
    }

    /// <summary>A planted item whose answer is <paramref name="truth"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="truth"/> is null.</exception>
    public static PoolAnswerBasis<TValue> ForPlant(TValue truth, string? truthSource = null)
    {
        ArgumentNullException.ThrowIfNull(truth);
        return new() { Purpose = PoolPurpose.Plant, Truth = truth, TruthSource = truthSource };
    }

    /// <summary>The same judge's earlier judgment of this input, asked again.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="earlier"/> is null.</exception>
    public static PoolAnswerBasis<TValue> ForRepeat(RecheckResult<TValue> earlier, Known<TValue>? known = null)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        return new() { Purpose = PoolPurpose.Repeat, Earlier = earlier, Known = known };
    }

    /// <summary>Another judge's earlier judgment of this decision, now judged by a second one.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="earlier"/> is null.</exception>
    public static PoolAnswerBasis<TValue> ForSecondOpinion(RecheckResult<TValue> earlier, Known<TValue>? known = null)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        return new() { Purpose = PoolPurpose.SecondOpinion, Earlier = earlier, Known = known };
    }
}

/// <summary>
/// Where one judge's answer goes: every destination that applies to it, made in one place. A destination that does not
/// apply is <see langword="null"/>.
/// </summary>
/// <typeparam name="TValue">The answer type.</typeparam>
public sealed record RoutedAnswer<TValue>
{
    /// <summary>
    /// The response to record in the pool, whatever the judge did. For a recheck it is what the pool turns into the tier's
    /// ledger record when its window is accounted.
    /// </summary>
    public required PoolResponse Response { get; init; }

    /// <summary>
    /// The judgment as a blind recheck result, for the measurements that pair judgments. Null when the judge could not tell:
    /// there is no judgment to pair.
    /// </summary>
    public RecheckResult<TValue>? Result { get; init; }

    /// <summary>For a planted item: the record that measures the judge. Null when the judge could not tell.</summary>
    public PlantRecord? Plant { get; init; }

    /// <summary>For a repeat: the record of the judge's consistency with itself. Null when the judge could not tell.</summary>
    public RepeatRecord? Repeat { get; init; }

    /// <summary>For a second opinion: the record of the two judges together. Null when the judge could not tell.</summary>
    public DoubleCheckRecord? DoubleCheck { get; init; }

    /// <summary>
    /// For an answered recheck: the answer a label is confirmed from (<c>LabelConfirmation.Decide</c>), stamped with the
    /// tier and the person who gave it. Its sampling weight is not known until the window is accounted — see
    /// <see cref="PoolAnswers.ReachedDraw"/>. Null for a pass, a could-not-tell, and every purpose but a recheck: only a recheck
    /// is a decision's answer.
    /// </summary>
    public ITracedPrediction<TValue>? Confirming { get; init; }
}

/// <summary>
/// One judge's answer to a pooled item, sent to everything it feeds. The four functions of <see cref="PoolAnswers"/> are
/// the pieces; this puts them together in one order, so a consumer cannot leave one out or count a could-not-tell as a pass.
/// </summary>
public static class PoolAnswerRouting
{
    /// <summary>
    /// Routes <paramref name="judgment"/>, given on <paramref name="shown"/> by <paramref name="reviewerKey"/> of
    /// <paramref name="checker"/>.
    /// </summary>
    /// <typeparam name="TValue">The answer type.</typeparam>
    /// <param name="shown">What was shown; its purpose says which destinations apply.</param>
    /// <param name="checker">The tier that judged — the rung of people.</param>
    /// <param name="reviewerKey">Who judged within it — a pseudonymous key.</param>
    /// <param name="judgment">What the judge did.</param>
    /// <param name="basis">What the answer is judged against; made for the purpose <paramref name="shown"/> carries.</param>
    /// <param name="policy">Which answers agree, and which are must-catch.</param>
    /// <param name="at">When.</param>
    /// <exception cref="ArgumentException">
    /// The basis is for another purpose than the item was shown for, or the earlier judgment of a repeat or a second opinion
    /// does not fit this one (another judge for a repeat, the same judge or another decision for a second opinion).
    /// </exception>
    public static RoutedAnswer<TValue> Route<TValue>(
        PoolPresentation shown, Tier checker, string reviewerKey, PoolJudgment<TValue> judgment, PoolAnswerBasis<TValue> basis,
        ISettlementPolicy<TValue> policy, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerKey);
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(policy);
        if (basis.Purpose != shown.Purpose)
            throw new ArgumentException($"The item was shown for {shown.Purpose}; the basis is for {basis.Purpose}.", nameof(basis));

        // A judge who could not tell gave no judgment: nothing to pair, nothing to score — only the response, which the
        // pool counts as a reach.
        if (judgment.Kind == PoolResponseKind.Unsure)
            return new() { Response = PoolAnswers.Unsure(shown, checker.Id, reviewerKey, at) };

        var verdict = judgment.Kind == PoolResponseKind.Answered
            ? JudgeVerdict<TValue>.Answer(judgment.Value!, judgedBy: reviewerKey)
            : JudgeVerdict<TValue>.Abstain(judgment.Reason ?? "passed", judgedBy: reviewerKey);

        // The settled answer is read for a recheck only; the other purposes have none.
        var response = PoolAnswers.Response(shown, checker.Id, reviewerKey, verdict, basis.Settled!, policy, at);
        var result = PoolAnswers.Result(shown, checker, verdict, at);
        var routed = new RoutedAnswer<TValue> { Response = response, Result = result };

        return shown.Purpose switch
        {
            PoolPurpose.Plant => routed with { Plant = PlantRecords.From(result, basis.Truth!, shown.SliceKey, policy, basis.TruthSource) },
            PoolPurpose.Repeat => routed with { Repeat = RepeatConsistency.Pair(basis.Earlier!, result, shown.SliceKey, policy, basis.Known) },
            PoolPurpose.SecondOpinion => routed with { DoubleCheck = DoubleCheckMeasure.Pair(basis.Earlier!, result, shown.SliceKey, policy, basis.Known) },
            _ => routed with { Confirming = Confirming(shown, checker, reviewerKey, judgment) },
        };
    }

    // The label confirmation reads who answered from the stamp: the tier alone would weigh a person as the whole rung, and
    // would not match the person's own repeat.
    private static ITracedPrediction<TValue>? Confirming<TValue>(PoolPresentation shown, Tier checker, string reviewerKey, PoolJudgment<TValue> judgment)
        => judgment.Kind != PoolResponseKind.Answered
            ? null
            : new TracedPrediction<TValue>(judgment.Value!, new PredictionProvenance
            {
                Kind = checker.Kind,
                TierId = checker.Id,
                TierVersion = checker.Version,
                JudgedBy = reviewerKey,
                SliceKey = shown.SliceKey,
            });
}
