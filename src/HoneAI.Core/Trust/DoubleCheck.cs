using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// One decision two judges judged blind, neither seeing the other — whether they agreed, whether each called it
/// must-catch, and, when the answer is known, whether each was right. Null where a judge gave no answer.
/// </summary>
/// <remarks>
/// The pair is unordered: <see cref="First"/> is the judge that sorts first (<see cref="JudgeIdentity.ToString"/>,
/// ordinal), so the same two judges always fill the same sides.
/// </remarks>
public sealed record DoubleCheckRecord
{
    public required string DecisionId { get; init; }

    public required JudgeIdentity First { get; init; }

    public required JudgeIdentity Second { get; init; }

    public required JudgeKind FirstKind { get; init; }

    public required JudgeKind SecondKind { get; init; }

    public required string SliceKey { get; init; }

    /// <summary>When the later of the two judgments was made.</summary>
    public required DateTimeOffset JudgedAt { get; init; }

    /// <summary>The two answers agree (by the settlement policy's predicate). Null unless both answered.</summary>
    public bool? Agreed { get; init; }

    /// <summary>The first judge answered a must-catch value. Null when it gave no answer.</summary>
    public bool? FirstMustCatch { get; init; }

    /// <summary>The second judge answered a must-catch value. Null when it gave no answer.</summary>
    public bool? SecondMustCatch { get; init; }

    /// <summary>The known answer is a must-catch value — only when the answer is known.</summary>
    public bool? TruthMustCatch { get; init; }

    /// <summary>The first judge was right — only when the answer is known and it answered.</summary>
    public bool? FirstCorrect { get; init; }

    /// <summary>The second judge was right — only when the answer is known and it answered.</summary>
    public bool? SecondCorrect { get; init; }
}

/// <summary>What two judges' blind answers to the same decisions show about them, over a set of double checks.</summary>
public sealed record DoubleCheckCell
{
    public required JudgeIdentity First { get; init; }

    public required JudgeIdentity Second { get; init; }

    /// <summary>
    /// Both judges are of one kind — two people, two models. Then disagreement measures how far judges of the kind
    /// spread; between kinds, what matters is whether their errors coincide.
    /// </summary>
    public required bool SameKind { get; init; }

    /// <summary>Double checks where both judges answered.</summary>
    public required int Pairs { get; init; }

    /// <summary>Share of those where the answers agree. Null without a pair.</summary>
    public double? Agreement { get; init; }

    /// <summary>
    /// Cohen's κ on the must-catch call — agreement on «must catch or not» beyond what the two judges' own rates of
    /// calling it would give by chance. Null without a pair, or when chance agreement is certain (both judges made
    /// one call throughout — there is nothing to agree beyond chance on).
    /// </summary>
    public double? MustCatchKappa { get; init; }

    /// <summary>Double checks with a known answer where both judges answered.</summary>
    public required int KnownPairs { get; init; }

    /// <summary>The first judge's error rate on the known pairs. Null without one.</summary>
    public double? FirstErrorRate { get; init; }

    /// <summary>The second judge's error rate on the known pairs. Null without one.</summary>
    public double? SecondErrorRate { get; init; }

    /// <summary>Known pairs both judges got wrong.</summary>
    public required int BothWrong { get; init; }

    /// <summary>
    /// How many more known pairs both got wrong than independent errors at these rates would give:
    /// (<see cref="BothWrong"/> / n) / (first error rate × second error rate). 1 is independent errors; above 1,
    /// the two fail on the same items and a check of one by the other catches less than its rates promise.
    /// Null when either judge made no error.
    /// </summary>
    public double? ErrorOverlap { get; init; }

    /// <summary>
    /// Exact one-sided p-value (hypergeometric, the error counts fixed) that the errors coincide at least this
    /// often if they are independent. Null when either judge made no error.
    /// </summary>
    public double? ErrorOverlapP { get; init; }

    /// <summary>Known must-catch pairs where both judges answered.</summary>
    public required int MustCatchKnown { get; init; }

    /// <summary>Of those, the ones neither judge called must-catch — what a check of one by the other cannot catch.</summary>
    public required int BothMissed { get; init; }
}

/// <summary>
/// Double checks (method ②): the same decision judged blind by two judges. Two of one kind — two people — measure
/// how far judges of that kind spread (κ); two of different kinds — a model and a language model — measure whether
/// their errors are orthogonal, which is what makes one a useful check on the other.
/// </summary>
public static class DoubleCheckMeasure
{
    /// <summary>
    /// The double check from two blind judgments of one decision. With <paramref name="known"/>, each is also scored
    /// against the known answer.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The two are of different decisions, by the same judge, or one was shown another answer.
    /// </exception>
    public static DoubleCheckRecord Pair<TValue>(
        RecheckResult<TValue> a, RecheckResult<TValue> b, string sliceKey, ISettlementPolicy<TValue> policy,
        Known<TValue>? known = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sliceKey);
        if (a.DecisionId != b.DecisionId)
            throw new ArgumentException("A double check is one decision judged by two judges.", nameof(b));
        if (a.Shown || b.Shown)
            throw new ArgumentException("A double check is blind: neither judge sees another answer.", a.Shown ? nameof(a) : nameof(b));

        var (first, second) = (IdentityOf(a), IdentityOf(b));
        if (first == second)
            throw new ArgumentException($"A double check takes two judges; both judgments are by '{first}'.", nameof(b));
        if (string.CompareOrdinal(first.ToString(), second.ToString()) > 0)
        {
            (a, b) = (b, a);
            (first, second) = (second, first);
        }

        bool? MustCatch(RecheckResult<TValue> r) => r.Verdict.Answered ? policy.MustCatch(r.Verdict.Value!) : null;
        bool? Correct(RecheckResult<TValue> r) => known is { } k && r.Verdict.Answered ? policy.Agrees(k.Value, r.Verdict.Value!) : null;

        return new DoubleCheckRecord
        {
            DecisionId = a.DecisionId,
            First = first,
            Second = second,
            FirstKind = a.Tier.Kind,
            SecondKind = b.Tier.Kind,
            SliceKey = sliceKey,
            JudgedAt = a.JudgedAt > b.JudgedAt ? a.JudgedAt : b.JudgedAt,
            Agreed = a.Verdict.Answered && b.Verdict.Answered ? policy.Agrees(a.Verdict.Value!, b.Verdict.Value!) : null,
            FirstMustCatch = MustCatch(a),
            SecondMustCatch = MustCatch(b),
            TruthMustCatch = known is { } t ? policy.MustCatch(t.Value) : null,
            FirstCorrect = Correct(a),
            SecondCorrect = Correct(b),
        };
    }

    /// <summary>The cell for the judges <paramref name="a"/> and <paramref name="b"/> (either order) over <paramref name="records"/>.</summary>
    public static DoubleCheckCell Compute(IEnumerable<DoubleCheckRecord> records, JudgeIdentity a, JudgeIdentity b)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (string.CompareOrdinal(a.ToString(), b.ToString()) > 0)
            (a, b) = (b, a);
        var mine = records.Where(r => r.First == a && r.Second == b).ToList();

        var answered = mine.Where(r => r.Agreed is not null).ToList();
        var known = answered.Where(r => r.FirstCorrect is not null && r.SecondCorrect is not null).ToList();
        var wrongFirst = known.Count(r => r.FirstCorrect == false);
        var wrongSecond = known.Count(r => r.SecondCorrect == false);
        var bothWrong = known.Count(r => r.FirstCorrect == false && r.SecondCorrect == false);
        var mustCatchKnown = known.Where(r => r.TruthMustCatch == true).ToList();

        double? overlap = null, overlapP = null;
        if (wrongFirst > 0 && wrongSecond > 0)
        {
            double n = known.Count;
            overlap = (bothWrong / n) / ((wrongFirst / n) * (wrongSecond / n));
            overlapP = HypergeometricUpperTail(bothWrong, known.Count, wrongFirst, wrongSecond);
        }

        return new DoubleCheckCell
        {
            First = a,
            Second = b,
            SameKind = mine.Count > 0 ? mine.All(r => r.FirstKind == r.SecondKind) : false,
            Pairs = answered.Count,
            Agreement = answered.Count > 0 ? answered.Count(r => r.Agreed == true) / (double)answered.Count : null,
            MustCatchKappa = Kappa(answered.Select(r => (r.FirstMustCatch!.Value, r.SecondMustCatch!.Value)).ToList()),
            KnownPairs = known.Count,
            FirstErrorRate = known.Count > 0 ? wrongFirst / (double)known.Count : null,
            SecondErrorRate = known.Count > 0 ? wrongSecond / (double)known.Count : null,
            BothWrong = bothWrong,
            ErrorOverlap = overlap,
            ErrorOverlapP = overlapP,
            MustCatchKnown = mustCatchKnown.Count,
            BothMissed = mustCatchKnown.Count(r => r.FirstMustCatch == false && r.SecondMustCatch == false),
        };
    }

    /// <summary>Cohen's κ for two binary calls. Null without a pair or when chance agreement is certain.</summary>
    public static double? Kappa(IReadOnlyCollection<(bool First, bool Second)> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0)
            return null;
        double n = calls.Count;
        var observed = calls.Count(c => c.First == c.Second) / n;
        var p1 = calls.Count(c => c.First) / n;
        var p2 = calls.Count(c => c.Second) / n;
        var chance = p1 * p2 + (1 - p1) * (1 - p2);
        return chance >= 1 ? null : (observed - chance) / (1 - chance);
    }

    /// <summary>
    /// P(X ≥ <paramref name="k"/>) for X hypergeometric: <paramref name="draws"/> items drawn from
    /// <paramref name="population"/> holding <paramref name="successes"/> — how often two independent error sets of
    /// these sizes overlap in at least <paramref name="k"/> items.
    /// </summary>
    public static double HypergeometricUpperTail(int k, int population, int successes, int draws)
    {
        if (population < 0 || successes < 0 || draws < 0 || successes > population || draws > population)
            throw new ArgumentOutOfRangeException(nameof(population), "Counts must fit within the population.");
        var low = Math.Max(0, draws + successes - population);
        var high = Math.Min(successes, draws);
        var logTotal = LogChoose(population, draws);
        double tail = 0;
        for (var x = Math.Max(k, low); x <= high; x++)
            tail += Math.Exp(LogChoose(successes, x) + LogChoose(population - successes, draws - x) - logTotal);
        return Math.Min(1, tail);
    }

    private static JudgeIdentity IdentityOf<TValue>(RecheckResult<TValue> r) => new(r.Tier.Id, r.Verdict.JudgedBy);

    private static double LogChoose(int n, int k) => LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k);

    private static double LogFactorial(int n)
    {
        double sum = 0;
        for (var i = 2; i <= n; i++)
            sum += Math.Log(i);
        return sum;
    }
}
