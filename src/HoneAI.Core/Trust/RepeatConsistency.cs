using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// One input one judge judged twice, blind both times — a language model asked again, a person shown the item
/// again later. Whether the two answers agree, whether the must-catch call held, and, when the answer is known,
/// whether each was right. Null where the judge gave no answer.
/// </summary>
public sealed record RepeatRecord
{
    /// <summary>The decision of the first judgment.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The judge that answered both times.</summary>
    public required JudgeIdentity Judge { get; init; }

    /// <summary>The kind of the judge.</summary>
    public required JudgeKind Kind { get; init; }

    /// <summary>The decision's slice.</summary>
    public required string SliceKey { get; init; }

    /// <summary>When the later of the two judgments was made.</summary>
    public required DateTimeOffset JudgedAt { get; init; }

    /// <summary>The two answers agree (by the settlement policy's predicate). Null unless both were answers.</summary>
    public bool? Consistent { get; init; }

    /// <summary>One answer was must-catch and the other not. Null unless both were answers.</summary>
    public bool? MustCatchFlipped { get; init; }

    /// <summary>The first answer was right — only when the answer is known and it answered.</summary>
    public bool? FirstCorrect { get; init; }

    /// <summary>The second answer was right — only when the answer is known and it answered.</summary>
    public bool? SecondCorrect { get; init; }
}

/// <summary>How steady one judge is on repeats, and what its errors are made of.</summary>
public sealed record RepeatCell
{
    /// <summary>The judge.</summary>
    public required JudgeIdentity Judge { get; init; }

    /// <summary>Repeats where both judgments were answers.</summary>
    public required int Repeats { get; init; }

    /// <summary>Share of those whose answers agree. Null without a repeat.</summary>
    public double? Consistency { get; init; }

    /// <summary>Repeats where the must-catch call flipped — the judge does not hold its own call on a risky item.</summary>
    public required int MustCatchFlips { get; init; }

    /// <summary>Repeats with a known answer where both judgments were answers.</summary>
    public required int KnownRepeats { get; init; }

    /// <summary>
    /// Share of known repeats answered the same and wrong both times — the judge's error that asking again does not
    /// move: a bias (a lean toward PASS, a criterion of its own), which more asking will not average away. Null
    /// without a known repeat.
    /// </summary>
    public double? SystematicErrorRate { get; init; }

    /// <summary>
    /// Share of known repeats whose answers differ — noise: asking again moves the answer, so a disagreeing repeat
    /// is a reason to send the item up rather than take either answer. Null without a known repeat.
    /// </summary>
    public double? NoiseRate { get; init; }

    /// <summary>Of the known repeats whose answers agree, the share right both times. Null without one.</summary>
    public double? AccuracyWhenConsistent { get; init; }
}

/// <summary>
/// Repeat consistency (method ④): the same judge, the same input, twice. How often it holds its answer, and on
/// known answers how its errors split between a bias it repeats and noise a repeat exposes.
/// </summary>
public static class RepeatConsistency
{
    /// <summary>
    /// The repeat from two blind judgments of one input by one judge. The second may be asked under another
    /// decision id — a judge that can tell a repeat is not repeating itself, it is remembering. With
    /// <paramref name="known"/>, both are scored against the known answer.
    /// </summary>
    /// <exception cref="ArgumentException">The two are by different judges, or one was shown another answer.</exception>
    public static RepeatRecord Pair<TValue>(
        RecheckResult<TValue> first, RecheckResult<TValue> second, string sliceKey, ISettlementPolicy<TValue> policy,
        Known<TValue>? known = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sliceKey);
        var judge = new JudgeIdentity(first.Tier.Id, first.Verdict.JudgedBy);
        if (judge != new JudgeIdentity(second.Tier.Id, second.Verdict.JudgedBy))
            throw new ArgumentException("A repeat is one judge judging one input twice.", nameof(second));
        if (first.Shown || second.Shown)
            throw new ArgumentException("A repeat is blind: the judge sees no other answer.", first.Shown ? nameof(first) : nameof(second));

        var both = first.Verdict.Answered && second.Verdict.Answered;
        bool? Correct(RecheckResult<TValue> r) => known is { } k && r.Verdict.Answered ? policy.Agrees(k.Value, r.Verdict.Value!) : null;

        return new RepeatRecord
        {
            DecisionId = first.DecisionId,
            Judge = judge,
            Kind = first.Tier.Kind,
            SliceKey = sliceKey,
            JudgedAt = first.JudgedAt > second.JudgedAt ? first.JudgedAt : second.JudgedAt,
            Consistent = both ? policy.Agrees(first.Verdict.Value!, second.Verdict.Value!) : null,
            MustCatchFlipped = both ? policy.MustCatch(first.Verdict.Value!) != policy.MustCatch(second.Verdict.Value!) : null,
            FirstCorrect = Correct(first),
            SecondCorrect = Correct(second),
        };
    }

    /// <summary>The cell for <paramref name="judge"/> over <paramref name="records"/>.</summary>
    public static RepeatCell Compute(IEnumerable<RepeatRecord> records, JudgeIdentity judge)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(judge);
        var answered = records.Where(r => r.Judge == judge && r.Consistent is not null).ToList();
        var known = answered.Where(r => r.FirstCorrect is not null && r.SecondCorrect is not null).ToList();
        var consistentKnown = known.Where(r => r.Consistent == true).ToList();

        return new RepeatCell
        {
            Judge = judge,
            Repeats = answered.Count,
            Consistency = answered.Count > 0 ? answered.Count(r => r.Consistent == true) / (double)answered.Count : null,
            MustCatchFlips = answered.Count(r => r.MustCatchFlipped == true),
            KnownRepeats = known.Count,
            SystematicErrorRate = known.Count > 0
                ? known.Count(r => r.Consistent == true && r.FirstCorrect == false && r.SecondCorrect == false) / (double)known.Count
                : null,
            NoiseRate = known.Count > 0 ? known.Count(r => r.Consistent == false) / (double)known.Count : null,
            AccuracyWhenConsistent = consistentKnown.Count > 0
                ? consistentKnown.Count(r => r.FirstCorrect == true && r.SecondCorrect == true) / (double)consistentKnown.Count
                : null,
        };
    }
}
