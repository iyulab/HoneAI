using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Trust;

namespace HoneAI.Learning;

/// <summary>
/// A metric measured on the held-out share for a model trained on <paramref name="Size"/> labels (or the weight they
/// stand for) — one point of a learning curve. The sizes are the consumer's choice; a set picked with
/// <see cref="TrainingSetSelector"/> at several budgets gives them.
/// </summary>
/// <param name="Size">The training set's size.</param>
/// <param name="Metric">The metric on the held-out share.</param>
/// <param name="StandardError">Its standard error, ≥ 0 — from the held-out share's independent units, not its label count.</param>
public sealed record SizeMeasurement(double Size, double Metric, double StandardError);

/// <summary>A training size the curve settles on, and why.</summary>
/// <param name="Size">The smallest size from which no larger one is significantly better; the largest measured when the curve is still rising.</param>
/// <param name="Plateaued">A larger size was measured and none of them is significantly better than <see cref="Size"/>.</param>
/// <param name="Reason">In words.</param>
public sealed record SizeRecommendation(double Size, bool Plateaued, string Reason);

/// <summary>
/// How large a training set needs to be: the smallest size beyond which the held-out metric does not go up. It is a
/// statistical call — a bigger set is only worth its cost where it is <em>shown</em> better — so the policy value is the
/// confidence it asks of the gain, and nothing in HoneAI sizes a set unless a consumer asks.
/// </summary>
public static class TrainingSizeCurve
{
    /// <summary>The recommended size over <paramref name="curve"/>.</summary>
    /// <param name="curve">At least one measurement, at distinct sizes (any order).</param>
    /// <param name="higherIsBetter">Whether a higher metric is better (accuracy, AUC) or lower (loss, error).</param>
    /// <param name="confidence">
    /// How sure a larger size must be to count as better, in (0.5, 1): one-sided. Higher asks more proof of a gain, so
    /// settles on smaller sets.
    /// </param>
    /// <exception cref="ArgumentException">No measurement, a repeated size, a size not above 0, or a standard error that is negative or not a number.</exception>
    public static SizeRecommendation Recommend(IEnumerable<SizeMeasurement> curve, bool higherIsBetter = true, double confidence = 0.95)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (!(confidence > 0.5 && confidence < 1))
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "A confidence lies in (0.5, 1).");
        var points = curve.OrderBy(m => m.Size).ToList();
        if (points.Count == 0)
            throw new ArgumentException("A curve has at least one measurement.", nameof(curve));
        foreach (var m in points)
            if (!(m.Size > 0) || double.IsInfinity(m.Size) || !(m.StandardError >= 0) || double.IsInfinity(m.StandardError) || double.IsNaN(m.Metric))
                throw new ArgumentException($"The measurement at size {m.Size} needs a size above 0, a metric and a standard error of 0 or more.", nameof(curve));
        if (points.Zip(points.Skip(1)).Any(pair => pair.First.Size == pair.Second.Size))
            throw new ArgumentException("A curve has one measurement per size.", nameof(curve));

        var z = TrustLedger.NormalQuantile(confidence);
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var better = points.Skip(i + 1).FirstOrDefault(larger => Gain(current, larger, higherIsBetter) > z * Math.Sqrt(
                current.StandardError * current.StandardError + larger.StandardError * larger.StandardError));
            if (better is null)
                return i == points.Count - 1
                    ? new(current.Size, false, $"{current.Size:0.##} is the largest measured, and the curve has no larger point to show a plateau")
                    : new(current.Size, true, $"no size above {current.Size:0.##} is better at {confidence:P0} confidence");
        }
        throw new InvalidOperationException("Unreachable: the last point always settles.");
    }

    private static double Gain(SizeMeasurement from, SizeMeasurement to, bool higherIsBetter)
        => higherIsBetter ? to.Metric - from.Metric : from.Metric - to.Metric;
}
