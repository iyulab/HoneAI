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
    /// in <paramref name="sliceKey"/> or across all slices when it is null.
    /// </summary>
    public static TeacherCell Compute(
        IEnumerable<PlantRecord> records,
        string tierId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? sliceKey = null,
        double confidence = TrustLedger.DefaultConfidence)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (confidence is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence lies strictly between 0 and 1.");

        var window = records
            .Where(r => r.TierId == tierId && r.JudgedAt >= from && r.JudgedAt < to && (sliceKey is null || r.SliceKey == sliceKey))
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
