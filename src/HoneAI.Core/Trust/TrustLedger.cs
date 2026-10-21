using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>
/// Computes a <see cref="LedgerCell"/> from the recheck records of one tier, slice and window.
/// </summary>
/// <remarks>
/// <para>
/// Each recheck stands for 1/π decisions like it, so agreement is the Horvitz–Thompson ratio of
/// agreed to answered weight — unbiased however the draw was tilted, as long as every π is
/// recorded and above zero. The interval is Wilson's, on the Kish effective sample size of those
/// weights. A recheck the tier above did not answer is not dropped: hard inputs fail more often,
/// so dropping them would flatter the tier. It counts as a disagreement at the interval's lower end
/// and as an agreement at its upper end.
/// </para>
/// <para>
/// The miss rate is conditional — of the must-catch decisions, the share the tier settled instead
/// of sending up. The ones it sent up and the tier above confirmed (<c>caught</c>) are all observed
/// and come in as a count; the ones it settled are seen only through rechecks and are estimated at
/// 1/π each. Its upper bound treats every failed recheck as a miss and finds the largest number of
/// misses still consistent, at the stated confidence, with seeing so few — an exact binomial bound
/// at the window's smallest π. With none seen this is ln(1/α)/π, the "zero misses is not zero"
/// rule: a tier that settled a few hundred decisions without a seen miss has not shown a 2% rate.
/// </para>
/// </remarks>
public static class TrustLedger
{
    /// <summary>The confidence intervals and bounds are stated at unless asked otherwise.</summary>
    public const double DefaultConfidence = 0.95;

    /// <summary>Computes the cell for <paramref name="tierId"/> × <paramref name="sliceKey"/> over [<paramref name="from"/>, <paramref name="to"/>).</summary>
    /// <param name="records">Recheck records; those outside the tier, slice or window are ignored.</param>
    /// <param name="caught">Must-catch decisions the tier sent up and the tier above confirmed in the window.</param>
    public static LedgerCell Compute(
        IEnumerable<RecheckRecord> records,
        string tierId,
        string sliceKey,
        DateTimeOffset from,
        DateTimeOffset to,
        int caught = 0,
        double confidence = DefaultConfidence,
        string? knob = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (caught < 0)
            throw new ArgumentOutOfRangeException(nameof(caught), caught, "The caught count cannot be negative.");
        if (confidence is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence lies strictly between 0 and 1.");

        var window = records
            .Where(r => r.TierId == tierId && r.SliceKey == sliceKey && r.DecidedAt >= from && r.DecidedAt < to)
            .ToList();
        if (knob is not null)
            window = KnobSubsample(window, knob);
        foreach (var r in window)
        {
            if (!(r.Probability > 0 && r.Probability <= 1))
                throw new ArgumentException(
                    $"Recheck of decision '{r.DecisionId}' has probability {r.Probability}; a recheck is drawn at a probability in (0, 1].",
                    nameof(records));
        }

        var answered = window.Where(r => r.Outcome != RecheckOutcome.UpperFailed).ToList();
        var failed = window.Count - answered.Count;

        double Weight(RecheckRecord r) => 1.0 / r.Probability;
        var answeredWeight = answered.Sum(Weight);
        var agreedWeight = answered.Where(r => r.Outcome == RecheckOutcome.Agreed).Sum(Weight);
        var failedWeight = window.Where(r => r.Outcome == RecheckOutcome.UpperFailed).Sum(Weight);
        var z = NormalQuantile(1 - (1 - confidence) / 2);

        double? agreement = null, lower = null, upper = null;
        if (answered.Count > 0)
            agreement = agreedWeight / answeredWeight;
        if (window.Count > 0)
        {
            var allWeight = answeredWeight + failedWeight;
            var nAll = EffectiveSize(window.Select(Weight));
            lower = Wilson(agreedWeight / allWeight, nAll, z).Lower;
            upper = Wilson((agreedWeight + failedWeight) / allWeight, nAll, z).Upper;
        }

        var misses = answered.Where(r => r.UpperMustCatch).ToList();
        var missesEstimated = misses.Sum(Weight);
        double? missRate = missesEstimated + caught > 0 ? missesEstimated / (missesEstimated + caught) : null;

        double? missRateUpper = null;
        if (window.Count > 0)
        {
            var seen = misses.Count + failed;
            var smallestPi = window.Min(r => r.Probability);
            var missesUpper = BinomialCountUpperBound(seen, smallestPi, 1 - confidence);
            if (missesUpper + caught > 0)
                missRateUpper = Math.Max(missesUpper / (missesUpper + caught), missRate ?? 0);
        }

        return new LedgerCell
        {
            TierId = tierId,
            SliceKey = sliceKey,
            WindowStart = from,
            WindowEnd = to,
            Rechecks = window.Count,
            UpperFailures = failed,
            EffectiveSampleSize = EffectiveSize(answered.Select(Weight)),
            Agreement = agreement,
            AgreementLower = lower,
            AgreementUpper = upper,
            MissesEstimated = missesEstimated,
            Caught = caught,
            MissRate = missRate,
            MissRateUpper = missRateUpper,
            Confidence = confidence,
            ConfidenceRankAuc = ConfidenceRankAuc(answered),
        };
    }

    /// <summary>
    /// The records <paramref name="knob"/> alone would have drawn, each re-weighted at that knob's
    /// rate. A decision is drawn when one uniform number — fixed by its id and snapshot version —
    /// falls under π, the largest knob rate; it falls under a knob's own rate exactly when that knob
    /// alone would have drawn it. So the sub-sample is a draw at the knob's rate, and the other
    /// knobs' tilt drops out. Records without knob rates or a snapshot version cannot be placed and
    /// are left out.
    /// </summary>
    public static List<RecheckRecord> KnobSubsample(IEnumerable<RecheckRecord> records, string knob)
    {
        var subsample = new List<RecheckRecord>();
        foreach (var r in records)
        {
            if (r.KnobRates is null || r.SnapshotVersion is null || !r.KnobRates.TryGetValue(knob, out var rate) || rate <= 0)
                continue;
            rate = Math.Min(rate, 1.0);
            if (HoneAI.Routing.RecheckDraw.Uniform(r.DecisionId, r.SnapshotVersion) < rate)
                subsample.Add(r with { Probability = rate, Knob = knob });
        }
        return subsample;
    }

    /// <summary>
    /// The window that stands for the last <paramref name="decisions"/> settled decisions before
    /// <paramref name="asOf"/>: the most recent rechecks, taken until their 1/π weights add up to
    /// <paramref name="decisions"/>. A cell over it is the "recent window" promise — a cumulative
    /// promise lags a tier that has moved.
    /// </summary>
    /// <returns>The window start (the oldest decision time taken), or null when there is no recheck.</returns>
    public static DateTimeOffset? RecentWindowStart(
        IEnumerable<RecheckRecord> records, string tierId, string sliceKey, DateTimeOffset asOf, double decisions)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (!(decisions > 0))
            throw new ArgumentOutOfRangeException(nameof(decisions), decisions, "A window stands for a positive number of decisions.");
        double covered = 0;
        DateTimeOffset? start = null;
        foreach (var r in records
                     .Where(r => r.TierId == tierId && r.SliceKey == sliceKey && r.DecidedAt < asOf && r.Probability > 0)
                     .OrderByDescending(r => r.DecidedAt))
        {
            start = r.DecidedAt;
            covered += 1.0 / r.Probability;
            if (covered >= decisions)
                break;
        }
        return start;
    }

    /// <summary>
    /// Weighted Mann–Whitney: the probability that a disagreed recheck's confidence is below an
    /// agreed one's, each pair weighted by the product of the two 1/π weights; ties count half.
    /// </summary>
    public static double? ConfidenceRankAuc(IEnumerable<RecheckRecord> answered)
    {
        var ranked = answered
            .Where(r => r.Confidence is { } c && !double.IsNaN(c) && r.Outcome != RecheckOutcome.UpperFailed)
            .Select(r => (Confidence: r.Confidence!.Value, Weight: 1.0 / r.Probability, Disagreed: r.Outcome == RecheckOutcome.Disagreed))
            .OrderBy(x => x.Confidence)
            .ToList();
        var disagreedTotal = ranked.Where(x => x.Disagreed).Sum(x => x.Weight);
        var agreedTotal = ranked.Where(x => !x.Disagreed).Sum(x => x.Weight);
        if (disagreedTotal == 0 || agreedTotal == 0)
            return null;

        // Sweep in confidence order: for each group of equal confidence, a disagreed record wins
        // against every agreed record above it and ties against the ones level with it.
        double agreedBelow = 0, favorable = 0;
        for (var i = 0; i < ranked.Count;)
        {
            var j = i;
            double groupAgreed = 0, groupDisagreed = 0;
            while (j < ranked.Count && ranked[j].Confidence == ranked[i].Confidence)
            {
                if (ranked[j].Disagreed) groupDisagreed += ranked[j].Weight; else groupAgreed += ranked[j].Weight;
                j++;
            }
            var agreedAbove = agreedTotal - agreedBelow - groupAgreed;
            favorable += groupDisagreed * (agreedAbove + 0.5 * groupAgreed);
            agreedBelow += groupAgreed;
            i = j;
        }
        return favorable / (disagreedTotal * agreedTotal);
    }

    /// <summary>Kish's effective sample size of weighted observations: (Σw)² / Σw².</summary>
    public static double EffectiveSize(IEnumerable<double> weights)
    {
        double sum = 0, sumSquares = 0;
        foreach (var w in weights)
        {
            sum += w;
            sumSquares += w * w;
        }
        return sumSquares == 0 ? 0 : sum * sum / sumSquares;
    }

    /// <summary>Wilson score interval for a proportion <paramref name="p"/> observed over <paramref name="n"/> (possibly fractional) trials.</summary>
    public static (double Lower, double Upper) Wilson(double p, double n, double z)
    {
        if (n <= 0)
            return (0, 1);
        var z2 = z * z;
        var denominator = 1 + z2 / n;
        var center = (p + z2 / (2 * n)) / denominator;
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / denominator;
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }

    /// <summary>
    /// The largest count <c>M</c> such that seeing at most <paramref name="seen"/> of <c>M</c>
    /// events, each seen with probability <paramref name="probability"/>, still has probability
    /// above <paramref name="alpha"/>. One-sided exact binomial bound on how many there were.
    /// </summary>
    public static double BinomialCountUpperBound(int seen, double probability, double alpha)
    {
        if (seen < 0) throw new ArgumentOutOfRangeException(nameof(seen));
        if (probability >= 1) return seen;

        // P(X ≤ seen | M) falls as M grows; find the last M where it is still above alpha.
        long low = seen, high = Math.Max(seen + 1, 1);
        while (BinomialCdf(seen, high, probability) > alpha)
        {
            low = high;
            high *= 2;
            if (high > 1L << 40)
                return double.PositiveInfinity;
        }
        while (high - low > 1)
        {
            var mid = low + (high - low) / 2;
            if (BinomialCdf(seen, mid, probability) > alpha) low = mid; else high = mid;
        }
        return low;
    }

    /// <summary>P(X ≤ k) for X ~ Binomial(n, p), summed in log space.</summary>
    public static double BinomialCdf(int k, long n, double p)
    {
        if (k >= n) return 1;
        if (p <= 0) return 1;
        var logP = Math.Log(p);
        var logQ = double.LogP1(-p);
        double total = 0;
        for (var i = 0; i <= k; i++)
            total += Math.Exp(LogChoose(n, i) + i * logP + (n - i) * logQ);
        return Math.Min(1, total);
    }

    private static double LogChoose(long n, long k) => LogGamma(n + 1) - LogGamma(k + 1) - LogGamma(n - k + 1);

    // Lanczos approximation (g = 7, n = 9), accurate to ~1e-15 for positive arguments.
    private static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    private static double LogGamma(double x)
    {
        if (x < 0.5)
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        x -= 1;
        var a = Lanczos[0];
        var t = x + 7.5;
        for (var i = 1; i < 9; i++)
            a += Lanczos[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    /// <summary>The standard normal quantile (Acklam's rational approximation, relative error &lt; 1.2e-9).</summary>
    public static double NormalQuantile(double p)
    {
        if (p is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(p), p, "A quantile lies strictly between 0 and 1.");

        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double low = 0.02425, high = 1 - low;

        if (p < low)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > high)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var r = p - 0.5;
        var s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r /
               (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }
}
