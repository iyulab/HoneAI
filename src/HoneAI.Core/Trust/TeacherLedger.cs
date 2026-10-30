using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// A teacher's standing from planted items. Agreement with a teacher is not correctness: a teacher
/// that waves through what it should catch lifts a lower tier that waves it through too, as
/// "agreeing". So a tier qualifies to teach only when its must-catch recall, measured on items whose
/// answer is known, clears a floor at its lower bound — the condition every gain of ground below it
/// rests on.
/// </summary>
public static class TeacherLedger
{
    /// <summary>
    /// The cell for <paramref name="tierId"/> over [<paramref name="from"/>, <paramref name="to"/>),
    /// in <paramref name="sliceKey"/> or across all slices when it is null, for one judge within the tier
    /// (<paramref name="judgedBy"/> — a person on a rung of people) or every judge of it pooled when that is null.
    /// </summary>
    public static TeacherCell Compute(
        IEnumerable<PlantRecord> records,
        string tierId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? sliceKey = null,
        double confidence = TrustLedger.DefaultConfidence,
        string? judgedBy = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (confidence is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence lies strictly between 0 and 1.");

        var window = records
            .Where(r => r.TierId == tierId && r.JudgedAt >= from && r.JudgedAt < to
                && (sliceKey is null || r.SliceKey == sliceKey) && (judgedBy is null || r.JudgedBy == judgedBy))
            .ToList();
        var alpha = 1 - confidence;

        var failures = window.Count(r => r.Outcome == PlantOutcome.Failed);
        var answered = window.Count - failures;
        var correct = window.Count(r => r.Outcome == PlantOutcome.Correct);

        var mustCatch = window.Where(r => r.TruthMustCatch).ToList();
        var mustCatchFailures = mustCatch.Count(r => r.Outcome == PlantOutcome.Failed);
        var caught = mustCatch.Count(r => r.Outcome != PlantOutcome.Failed && r.AnsweredMustCatch);
        var mustCatchAnswered = mustCatch.Count - mustCatchFailures;

        return new TeacherCell
        {
            TierId = tierId,
            JudgedBy = judgedBy,
            SliceKey = sliceKey,
            WindowStart = from,
            WindowEnd = to,
            Plants = window.Count,
            Failures = failures,
            MustCatchPlants = mustCatch.Count,
            MustCatchCaught = caught,
            MustCatchFailures = mustCatchFailures,
            Recall = mustCatchAnswered > 0 ? (double)caught / mustCatchAnswered : null,
            RecallLower = mustCatch.Count > 0 ? ClopperPearsonLower(caught, mustCatch.Count, alpha) : null,
            Accuracy = answered > 0 ? (double)correct / answered : null,
            AccuracyLower = window.Count > 0 ? ClopperPearsonLower(correct, window.Count, alpha) : null,
            Confidence = confidence,
        };
    }

    /// <summary>
    /// Whether <paramref name="cell"/> qualifies its tier to teach: its must-catch recall lower bound
    /// is at least <paramref name="minimumRecall"/>. A teacher with no must-catch plant has shown
    /// nothing and does not qualify.
    /// </summary>
    public static bool Qualifies(TeacherCell cell, double minimumRecall)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell.RecallLower is { } lower && lower >= minimumRecall;
    }

    /// <summary>
    /// The fewest must-catch plants from which a teacher that really catches <paramref name="expectedRecall"/> of them qualifies
    /// against <paramref name="minimumRecall"/> (<see cref="Qualifies"/>, bound stated at <paramref name="confidence"/>) with at least
    /// <paramref name="power"/> probability — and keeps doing so for every larger count up to <paramref name="maxPlants"/>, because the
    /// exact bound moves in steps and a single lucky count is not a plan. Null when no count up to the cap reaches it, which is always
    /// so for a teacher whose recall is no higher than the floor.
    /// </summary>
    /// <remarks>
    /// The count is for one cell. A cell pooled over slices is blind to a miss confined to one of them, and each slice's own cell needs
    /// this many must-catch plants of its own — size the plants per slice the teacher is to be trusted on.
    /// </remarks>
    public static int? MustCatchPlantsToQualify(
        double expectedRecall,
        double minimumRecall,
        double confidence = TrustLedger.DefaultConfidence,
        double power = 0.8,
        int maxPlants = 2000)
    {
        if (!(expectedRecall > 0 && expectedRecall <= 1))
            throw new ArgumentOutOfRangeException(nameof(expectedRecall), expectedRecall, "A recall lies in (0, 1].");
        if (!(minimumRecall > 0 && minimumRecall < 1))
            throw new ArgumentOutOfRangeException(nameof(minimumRecall), minimumRecall, "A recall floor lies strictly between 0 and 1.");
        if (confidence is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence lies strictly between 0 and 1.");
        if (power is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(power), power, "Power lies strictly between 0 and 1.");
        if (maxPlants < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPlants), maxPlants, "At least one plant.");
        if (expectedRecall <= minimumRecall)
            return null;

        var alpha = 1 - confidence;
        int? from = null;
        for (var m = 1; m <= maxPlants; m++)
        {
            if (QualifyingChance(m, expectedRecall, minimumRecall, alpha) >= power) from ??= m;
            else from = null;
        }
        return from;
    }

    // P(a teacher of recall `recall` qualifies on `plants` must-catch plants): the smallest catch count whose lower bound clears
    // the floor is the smallest k with P(X >= k | floor) <= alpha, and the teacher qualifies when it catches that many.
    private static double QualifyingChance(int plants, double recall, double floor, double alpha)
    {
        var atFloor = LogPmfDescending(plants, floor);
        var tail = 0.0;
        var needed = plants + 1;
        for (var k = plants; k >= 0; k--)
        {
            tail += Math.Exp(atFloor(k));
            if (tail > alpha) break;
            needed = k;
        }
        if (needed > plants)
            return 0;
        if (recall >= 1)
            return 1;

        var atRecall = LogPmfDescending(plants, recall);
        var chance = 0.0;
        for (var k = plants; k >= needed; k--)
            chance += Math.Exp(atRecall(k));
        return Math.Min(1, chance);
    }

    // log P(X = k) for X ~ Binomial(n, p), asked for k = n, n − 1, …, in that order (each step is one multiplication on the last).
    private static Func<int, double> LogPmfDescending(int n, double p)
    {
        var logP = Math.Log(p);
        var logQ = double.LogP1(-p);
        var last = n;
        var value = n * logP;
        return k =>
        {
            while (last > k)
            {
                value += Math.Log((double)last / (n - last + 1)) + logQ - logP;
                last--;
            }
            return value;
        };
    }

    /// <summary>
    /// The exact (Clopper–Pearson) one-sided lower bound on a proportion after <paramref name="successes"/>
    /// in <paramref name="trials"/>: the smallest p at which seeing that many or more still has
    /// probability <paramref name="alpha"/>. Zero with no success.
    /// </summary>
    public static double ClopperPearsonLower(int successes, int trials, double alpha)
    {
        if (trials <= 0) throw new ArgumentOutOfRangeException(nameof(trials), trials, "At least one trial.");
        if (successes < 0 || successes > trials) throw new ArgumentOutOfRangeException(nameof(successes));
        if (successes == 0)
            return 0;
        if (successes == trials)
            return Math.Pow(alpha, 1.0 / trials);

        // P(X ≥ k | p) = 1 − CDF(k − 1; n, p) rises with p; find where it reaches alpha.
        double low = 0, high = 1;
        for (var i = 0; i < 100 && high - low > 1e-12; i++)
        {
            var mid = (low + high) / 2;
            if (1 - TrustLedger.BinomialCdf(successes - 1, trials, mid) < alpha) low = mid; else high = mid;
        }
        return low;
    }
}

/// <summary>Turns a teacher's judgment of a planted item into a <see cref="PlantRecord"/>.</summary>
public static class PlantRecords
{
    /// <summary>
    /// The record of <paramref name="judgment"/> on a planted item whose known answer is
    /// <paramref name="truth"/>. Judge the item through <see cref="TieredRouter{TInput,TValue}.RecheckAsync"/>,
    /// with the plant's id as the decision id, so the teacher sees it exactly as it sees a real recheck.
    /// </summary>
    public static PlantRecord From<TValue>(
        RecheckResult<TValue> judgment, TValue truth, string sliceKey, ISettlementPolicy<TValue> policy, string? truthSource = null)
    {
        ArgumentNullException.ThrowIfNull(judgment);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sliceKey);
        if (judgment.Shown)
            throw new ArgumentException("The plant was judged shown the lower answer; a teacher is measured blind.", nameof(judgment));
        var verdict = judgment.Verdict;
        return new PlantRecord
        {
            PlantId = judgment.DecisionId,
            TierId = judgment.Tier.Id,
            JudgedBy = verdict.JudgedBy,
            TierVersion = judgment.Tier.Version,
            SliceKey = sliceKey,
            JudgedAt = judgment.JudgedAt,
            TruthMustCatch = policy.MustCatch(truth),
            Outcome = !verdict.Answered ? PlantOutcome.Failed
                : policy.Agrees(truth, verdict.Value!) ? PlantOutcome.Correct : PlantOutcome.Wrong,
            AnsweredMustCatch = verdict.Answered && policy.MustCatch(verdict.Value!),
            TruthSource = truthSource,
        };
    }
}
