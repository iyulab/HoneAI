using System;
using System.Collections.Generic;

namespace HoneAI.Routing;

/// <summary>
/// Turns a decision's selection signal into its recheck rate for one knob. Selection is a design of
/// probabilities, not a choice of items: every decision keeps a rate above zero and the rate is
/// recorded, so however the draw is tilted, a 1/π count stays unbiased — the tilt changes only how
/// precise it is.
/// </summary>
public static class SelectionScore
{
    /// <summary>
    /// The calibrated probability that the settling tier is wrong at <paramref name="risk"/>: the
    /// rate measured in the first bin whose upper end reaches it (the last bin above its end).
    /// Null when there is no calibration.
    /// </summary>
    public static double? Calibrate(IReadOnlyList<RiskBin> calibration, double risk)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (calibration.Count == 0 || double.IsNaN(risk))
            return null;
        RiskBin? hit = null, last = null;
        foreach (var bin in calibration)
        {
            if (risk <= bin.UpTo && (hit is null || bin.UpTo < hit.UpTo))
                hit = bin;
            if (last is null || bin.UpTo > last.UpTo)
                last = bin;
        }
        return Math.Clamp((hit ?? last)!.Probability, 0, 1);
    }

    /// <summary>The score of calibrated risk <paramref name="p"/> and weight <paramref name="weight"/> under <paramref name="shape"/>.</summary>
    public static double Score(SelectionShape shape, double p, double weight)
    {
        p = Math.Clamp(p, 0, 1);
        weight = Math.Max(weight, 0);
        return shape switch
        {
            SelectionShape.Variance => Math.Sqrt(p * (1 - p)),
            SelectionShape.WeightedVariance => weight * Math.Sqrt(p * (1 - p)),
            SelectionShape.WeightedRisk => weight * p,
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
    }

    /// <summary>
    /// The score of a decision under <paramref name="selection"/>, or null when it cannot be scored —
    /// no calibration yet. A decision without a risk scores the mean (weighted, where the shape weighs);
    /// either way a damped cluster's factor applies.
    /// </summary>
    public static double? ScoreOf(KnobSelection selection, SelectionSignal? signal)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Calibration.Count == 0 || !(selection.MeanScore > 0))
            return null;
        var weight = signal?.Weight ?? 1;
        var weighs = selection.Shape != SelectionShape.Variance;
        var damping = signal?.Cluster is { } cluster && selection.ClusterFactors is { } factors && factors.TryGetValue(cluster, out var f)
            ? Math.Clamp(f, 0, 1) : 1;
        if (signal?.Risk is not { } risk || Calibrate(selection.Calibration, risk) is not { } p)
            return damping * selection.MeanScore * (weighs ? Math.Max(weight, 0) : 1);
        return damping * Score(selection.Shape, p, weight);
    }

    /// <summary>
    /// The rate a decision is drawn at by a knob whose expected rate is <paramref name="knobRate"/>:
    /// <c>rate × ((1 − τ) · score ÷ mean + τ)</c>, capped at 1. Without a selection, or before the
    /// signal is calibrated, or for a decision with no signal at all, the knob's own rate.
    /// </summary>
    public static double Rate(double knobRate, KnobSelection? selection, SelectionSignal? signal)
        => selection is null || Priority(selection, signal) is not { } priority ? knobRate : Math.Min(1, knobRate * priority);

    /// <summary>
    /// How much more (or less) than an ordinary decision a decision is worth picking under <paramref name="selection"/>:
    /// <c>(1 − τ) · score ÷ mean + τ</c> — 1 is ordinary, the uniform mix τ keeps every decision above zero. Null when
    /// there is no signal or the selection is not calibrated yet. The same priority sets the recheck rate
    /// (<see cref="Rate"/>) and the odds a label is kept when a training set is thinned (<c>TrainingSetSelector</c>).
    /// </summary>
    public static double? Priority(KnobSelection selection, SelectionSignal? signal)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (signal is null || ScoreOf(selection, signal) is not { } score)
            return null;
        var tau = Math.Clamp(selection.UniformMix, 0, 1);
        return (1 - tau) * score / selection.MeanScore + tau;
    }
}
