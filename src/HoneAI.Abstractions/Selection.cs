using System.Collections.Generic;

namespace HoneAI;

/// <summary>
/// What is known about one settled decision that bears on whether it is worth a recheck. The
/// consumer or an engine supplies it through <see cref="ISelectionSignals{TInput,TValue}"/>; HoneAI
/// only turns it into a recheck probability.
/// </summary>
public sealed record SelectionSignal
{
    /// <summary>
    /// How likely the settling tier is to be wrong here, on any scale where higher means more
    /// likely — a confidence complement, a disagreement between cheap tiers, a novelty score. It is
    /// used only through the snapshot's calibration (<see cref="KnobSelection.Calibration"/>), which
    /// maps it to a measured disagreement rate. Null when there is none.
    /// </summary>
    public double? Risk { get; init; }

    /// <summary>
    /// How bad a mistake here would be (≥ 0, 1 = ordinary) — closeness to a must-catch value, the
    /// content's severity, the subject's importance.
    /// </summary>
    public double Weight { get; init; } = 1;

    /// <summary>A meaning cluster the input belongs to, when the consumer has one.</summary>
    public string? Cluster { get; init; }
}

/// <summary>What a selection-signal source sees of a settled decision.</summary>
/// <typeparam name="TValue">The answer type.</typeparam>
public sealed record SelectionContext<TValue>
{
    /// <summary>The decision.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The input's slice.</summary>
    public required string SliceKey { get; init; }

    /// <summary>The tier that settled it.</summary>
    public required Tier Tier { get; init; }

    /// <summary>The settled answer.</summary>
    public required TValue Value { get; init; }

    /// <summary>The settling tier's confidence, when it gave one.</summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// Every tier's verdict on the way up, lowest first — including tiers in shadow, whose answers
    /// cost nothing more and disagree with the settled one exactly where it is in doubt.
    /// </summary>
    public required IReadOnlyList<(Tier Tier, JudgeVerdict<TValue> Verdict)> Verdicts { get; init; }
}

/// <summary>
/// The selection-signal port: the consumer's or engine's knowledge of risk and meaning. Called once
/// per settled decision before it is drawn; must be quick and must not call a judge.
/// </summary>
public interface ISelectionSignals<in TInput, TValue>
{
    /// <summary>The signal for <paramref name="input"/>, or null when there is nothing to say.</summary>
    SelectionSignal? SignalOf(TInput input, SelectionContext<TValue> context);
}

/// <summary>How a knob turns a decision's calibrated risk p and weight w into a relative score.</summary>
public enum SelectionShape
{
    /// <summary>√(p(1−p)) — the variance-minimizing share for estimating agreement. Weight ignored.</summary>
    Variance = 0,

    /// <summary>w·√(p(1−p)) — for estimating misses of what matters: the slice-watch knob.</summary>
    WeightedVariance = 1,

    /// <summary>w·p — the likeliest costly mistakes first: harvesting labels.</summary>
    WeightedRisk = 2,
}

/// <summary>One step of a risk calibration: raw risk up to <paramref name="UpTo"/> was measured to disagree at <paramref name="Probability"/>.</summary>
/// <param name="UpTo">Inclusive upper end of the raw-risk interval (the last bin covers everything above too).</param>
/// <param name="Probability">The disagreement rate measured in it, in [0, 1].</param>
public sealed record RiskBin(double UpTo, double Probability);

/// <summary>
/// How one knob tilts its recheck probability toward the decisions worth checking. The knob's rate
/// stays its expected rate: each decision is drawn at rate × ((1 − τ) · score ÷ mean score + τ), so a
/// tilt moves rechecks between decisions without changing how many there are (bar the cap at 1).
/// </summary>
public sealed record KnobSelection
{
    /// <summary>How the score is formed.</summary>
    public required SelectionShape Shape { get; init; }

    /// <summary>
    /// Raw risk → measured disagreement, ascending by <see cref="RiskBin.UpTo"/>. Empty means the
    /// signal is not calibrated yet, and the knob draws uniformly — a signal is not trusted before it
    /// is measured.
    /// </summary>
    public required IReadOnlyList<RiskBin> Calibration { get; init; }

    /// <summary>
    /// The mean score over the slice's settled decisions, the normalizer that keeps the expected rate
    /// at the knob's rate. A decision without a risk scores this mean (times its weight, where the
    /// shape weighs) — typical risk, as far as anyone knows.
    /// </summary>
    public required double MeanScore { get; init; }

    /// <summary>
    /// τ in [0, 1]: the share drawn uniformly whatever the signal says. Larger when the signal has
    /// measured worse, so a selection is never much worse than uniform.
    /// </summary>
    public required double UniformMix { get; init; }
}
