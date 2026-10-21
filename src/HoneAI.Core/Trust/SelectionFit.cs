using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// Fits a knob's <see cref="KnobSelection"/> from what was measured: the calibration from rechecks,
/// the mean score from every settled decision, and the uniform mix τ from how well the signal ranked
/// mistakes on rechecks it was not fitted on. A signal is not trusted before it is measured, and is
/// trusted only as far as it measured well.
/// </summary>
/// <remarks>
/// <para>
/// <b>Calibration.</b> The answered rechecks are cut into bins of equal effective size by raw risk;
/// each bin's disagreement rate is the 1/π-weighted share of disagreed rechecks (unbiased however the
/// rechecks were drawn), shrunk toward the overall rate by <see cref="SelectionFitOptions.PriorStrength"/>
/// pseudo-observations so a small bin cannot claim zero, then made non-decreasing in risk by pooling
/// adjacent violators — the signal's contract is that higher means likelier wrong.
/// </para>
/// <para>
/// <b>Validity → τ.</b> The rechecks are split in two by a hash of the decision id; a calibration fitted
/// on each half ranks the other half (1/π-weighted AUC of disagreement over the calibrated risk). At
/// or below chance the signal is off (τ = 1, uniform). Above it, τ = max(minimum, 1 − 2·(AUC − 0.5)),
/// so a signal that ranks perfectly still leaves the minimum uniform share — a selection is then never
/// much worse than uniform (Li, Zrnic and Candès, robust sampling for active inference).
/// </para>
/// </remarks>
public static class SelectionFit
{
    /// <summary>
    /// Fits the selection for one knob of one tier and slice.
    /// </summary>
    /// <param name="rechecks">The cell's recheck records (one tier, slice and window — filter as for <see cref="TrustLedger.Compute"/>).</param>
    /// <param name="decided">The signals of every decision the tier settled in that cell, drawn or not (<see cref="SampleDraw.Signal"/>); null for one without a signal.</param>
    /// <param name="shape">The knob's shape.</param>
    /// <param name="options">Bin count, minimum sizes, prior strength, minimum τ.</param>
    public static SelectionFitResult Fit(
        IEnumerable<RecheckRecord> rechecks,
        IEnumerable<SelectionSignal?> decided,
        SelectionShape shape,
        SelectionFitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rechecks);
        ArgumentNullException.ThrowIfNull(decided);
        options ??= new SelectionFitOptions();

        var observed = rechecks
            .Where(r => r.Outcome != RecheckOutcome.UpperFailed && r.Risk is { } risk && !double.IsNaN(risk) && r.Probability > 0)
            .Select(r => new Observation(r.DecisionId, r.Risk!.Value, 1.0 / r.Probability, r.Outcome == RecheckOutcome.Disagreed))
            .ToList();
        var disagreed = observed.Count(o => o.Disagreed);

        if (observed.Count < options.MinimumRechecks || disagreed < options.MinimumDisagreements)
            return Uncalibrated(shape, observed.Count, disagreed,
                $"{observed.Count} answered rechecks with a risk and {disagreed} disagreements — "
                + $"at least {options.MinimumRechecks} and {options.MinimumDisagreements} are needed before the signal is trusted");

        var calibration = Calibrate(observed, options);

        // Validity on rechecks the calibration was not fitted on.
        var (a, b) = (observed.Where(o => Fold(o.DecisionId)).ToList(), observed.Where(o => !Fold(o.DecisionId)).ToList());
        var held = new List<(double Score, double Weight, bool Disagreed)>();
        if (a.Count > 0 && b.Count > 0 && a.Any(o => o.Disagreed) && b.Any(o => o.Disagreed))
        {
            var onA = Calibrate(a, options);
            var onB = Calibrate(b, options);
            held.AddRange(b.Select(o => (SelectionScore.Calibrate(onA, o.Risk)!.Value, o.Weight, o.Disagreed)));
            held.AddRange(a.Select(o => (SelectionScore.Calibrate(onB, o.Risk)!.Value, o.Weight, o.Disagreed)));
        }
        var auc = TrustLedger.RankAuc(held);
        var tau = auc is not { } v || v <= 0.5 ? 1.0 : Math.Clamp(1 - 2 * (v - 0.5), options.MinimumUniformMix, 1);

        var decisions = decided.ToList();
        var probe = new KnobSelection { Shape = shape, Calibration = calibration, MeanScore = 1, UniformMix = tau };
        if (MeanScore(decisions, probe, null) is not { } mean)
            return Uncalibrated(shape, observed.Count, disagreed,
                "no settled decision with a risk to normalize the scores over");

        // Representativeness: a cluster the tilt would send more than the cap of rechecks in the window
        // is damped to the cap. Only the tilted share is capped — the uniform share τ is proportional to
        // a cluster's size, representative already, and stays whatever the signal says. The damping
        // lowers the mean, which raises everyone else's rate, so fit both together.
        IReadOnlyDictionary<string, double>? factors = null;
        if (options.ClusterRecheckCap is { } cap)
        {
            if (options.KnobRate is not { } rate || !(rate is > 0 and <= 1))
                throw new ArgumentException("A cluster cap needs the knob's rate (SelectionFitOptions.KnobRate) to count draws by.", nameof(options));
            var byCluster = decisions
                .Where(d => d?.Cluster is not null)
                .GroupBy(d => d!.Cluster!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            // Expected tilted draws of a cluster at a mean: Σ rate·(1 − τ)·score/mean, before damping.
            double Tilted(List<SelectionSignal?> members, double m)
                => members.Sum(x => rate * (1 - tau) * SelectionScore.ScoreOf(probe with { MeanScore = m }, x)!.Value / m);

            // The mean moves with the factors (a damped half of the population lowers it a lot), so
            // step each factor toward the cap by the square root of the miss, re-measuring at the mean
            // the factors give, until every damped cluster sits within 1% of the cap.
            var current = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var settled = true;
                foreach (var (cluster, members) in byCluster)
                {
                    var factor = current.GetValueOrDefault(cluster, 1.0);
                    var realized = factor * Tilted(members, mean);
                    if (realized <= cap && factor >= 1)
                        continue;
                    var stepped = Math.Clamp(factor * Math.Sqrt(cap / Math.Max(realized, 1e-12)), options.MinimumClusterFactor, 1);
                    if (Math.Abs(realized - cap) > 0.01 * cap && stepped != factor)
                        settled = false;
                    if (stepped >= 1) current.Remove(cluster); else current[cluster] = stepped;
                }
                mean = MeanScore(decisions, probe, current) ?? mean;
                if (settled)
                    break;
            }
            factors = current.Count > 0 ? current : null;
        }

        var reason = auc is null
            ? "the halves could not both be scored — the signal is drawn uniformly until they can"
            : auc <= 0.5
                ? $"the signal ranked mistakes no better than chance on held-out rechecks (AUC {auc:F3})"
                : $"held-out AUC {auc:F3}";
        return new SelectionFitResult
        {
            Selection = probe with { MeanScore = mean, ClusterFactors = factors },
            Rechecks = observed.Count,
            Disagreements = disagreed,
            HeldOutRankAuc = auc,
            Reason = reason,
        };
    }

    // The mean score over the decisions, damping included. A decision without a risk scores the mean
    // itself (times its weight where the shape weighs, times its damping), so M solves
    // M = (Σ scored + M · Σ unscored) / N.
    private static double? MeanScore(List<SelectionSignal?> decisions, KnobSelection probe, IReadOnlyDictionary<string, double>? factors)
    {
        double scoredSum = 0, unscored = 0;
        foreach (var signal in decisions)
        {
            var damping = signal?.Cluster is { } c && factors is not null && factors.TryGetValue(c, out var f) ? f : 1;
            if (signal?.Risk is { } risk && !double.IsNaN(risk))
                scoredSum += damping * SelectionScore.Score(probe.Shape, SelectionScore.Calibrate(probe.Calibration, risk)!.Value, signal.Weight);
            else
                unscored += damping * (probe.Shape == SelectionShape.Variance ? 1 : Math.Max(signal?.Weight ?? 1, 0));
        }
        var denominator = decisions.Count - unscored;
        return decisions.Count > 0 && denominator > 0 && scoredSum > 0 ? scoredSum / denominator : null;
    }

    private static SelectionFitResult Uncalibrated(SelectionShape shape, int rechecks, int disagreed, string reason) => new()
    {
        Selection = new KnobSelection { Shape = shape, Calibration = [], MeanScore = 0, UniformMix = 1 },
        Rechecks = rechecks,
        Disagreements = disagreed,
        Reason = reason,
    };

    private sealed record Observation(string DecisionId, double Risk, double Weight, bool Disagreed);

    // A stable split: the same decision falls in the same half on every fit.
    private static bool Fold(string decisionId) => RecheckDraw.Uniform(decisionId, "selection-fit") < 0.5;

    private static IReadOnlyList<RiskBin> Calibrate(List<Observation> observations, SelectionFitOptions options)
    {
        var ordered = observations.OrderBy(o => o.Risk).ToList();
        var overall = ordered.Sum(o => o.Disagreed ? o.Weight : 0) / ordered.Sum(o => o.Weight);

        // Bins of roughly equal effective size, never splitting equal risks, each at least the minimum.
        var totalSize = TrustLedger.EffectiveSize(ordered.Select(o => o.Weight));
        var target = Math.Max(totalSize / Math.Max(options.Bins, 1), options.MinimumBinSize);
        var bins = new List<(double UpTo, double Weight, double DisagreedWeight, double Size)>();
        var current = new List<Observation>();
        for (var i = 0; i < ordered.Count; i++)
        {
            current.Add(ordered[i]);
            var atBoundary = i == ordered.Count - 1 || ordered[i + 1].Risk != ordered[i].Risk;
            if (atBoundary && TrustLedger.EffectiveSize(current.Select(o => o.Weight)) >= target)
            {
                bins.Add(Summarize(current));
                current = [];
            }
        }
        if (current.Count > 0)
        {
            if (bins.Count == 0)
                bins.Add(Summarize(current));
            else
            {
                var last = bins[^1];
                var tail = Summarize(current);
                bins[^1] = (tail.UpTo, last.Weight + tail.Weight, last.DisagreedWeight + tail.DisagreedWeight, last.Size + tail.Size);
            }
        }

        // Shrink each bin's rate toward the overall rate, then pool adjacent violators so the rate
        // never falls as risk rises.
        var blocks = bins.Select(b => new Block(
            b.UpTo,
            (b.Size * (b.DisagreedWeight / b.Weight) + options.PriorStrength * overall) / (b.Size + options.PriorStrength),
            b.Size + options.PriorStrength)).ToList();
        for (var i = 1; i < blocks.Count;)
        {
            if (blocks[i].Rate >= blocks[i - 1].Rate)
            {
                i++;
                continue;
            }
            var merged = new Block(
                blocks[i].UpTo,
                (blocks[i - 1].Rate * blocks[i - 1].Size + blocks[i].Rate * blocks[i].Size) / (blocks[i - 1].Size + blocks[i].Size),
                blocks[i - 1].Size + blocks[i].Size);
            blocks[i - 1] = merged;
            blocks.RemoveAt(i);
            i = Math.Max(i - 1, 1);
        }
        return blocks.Select(b => new RiskBin(b.UpTo, b.Rate)).ToArray();
    }

    private sealed record Block(double UpTo, double Rate, double Size);

    private static (double UpTo, double Weight, double DisagreedWeight, double Size) Summarize(List<Observation> bin)
        => (bin[^1].Risk, bin.Sum(o => o.Weight), bin.Sum(o => o.Disagreed ? o.Weight : 0), TrustLedger.EffectiveSize(bin.Select(o => o.Weight)));
}

/// <summary>Options for <see cref="SelectionFit.Fit"/>.</summary>
public sealed record SelectionFitOptions
{
    /// <summary>How many risk bins to aim for. Default 5.</summary>
    public int Bins { get; init; } = 5;

    /// <summary>The least effective size a bin may have. Default 30.</summary>
    public double MinimumBinSize { get; init; } = 30;

    /// <summary>Pseudo-observations at the overall rate each bin is shrunk by. Default 10.</summary>
    public double PriorStrength { get; init; } = 10;

    /// <summary>Answered rechecks with a risk needed before the signal is calibrated. Default 100.</summary>
    public int MinimumRechecks { get; init; } = 100;

    /// <summary>Disagreements among them needed before the signal is calibrated. Default 10.</summary>
    public int MinimumDisagreements { get; init; } = 10;

    /// <summary>The least uniform share τ, however well the signal ranks. Default 0.1.</summary>
    public double MinimumUniformMix { get; init; } = 0.1;

    /// <summary>
    /// The most rechecks the selection's tilt should send to one meaning cluster over the decisions
    /// fitted on (one window); clusters above it are damped (<see cref="KnobSelection.ClusterFactors"/>).
    /// The uniform share τ is not counted — it is proportional to a cluster's size already. Null (the
    /// default) leaves clusters undamped. Needs <see cref="KnobRate"/>.
    /// </summary>
    public double? ClusterRecheckCap { get; init; }

    /// <summary>The knob's expected rate — what draws are counted by for <see cref="ClusterRecheckCap"/>.</summary>
    public double? KnobRate { get; init; }

    /// <summary>The least a cluster's score is damped to. Default 0.05 — damping thins a cluster, it never empties it.</summary>
    public double MinimumClusterFactor { get; init; } = 0.05;
}

/// <summary>A fitted selection and what it rests on.</summary>
public sealed record SelectionFitResult
{
    /// <summary>The selection to put in the snapshot. Uncalibrated (draws uniformly) when the evidence is short.</summary>
    public required KnobSelection Selection { get; init; }

    /// <summary>Answered rechecks with a risk the fit used.</summary>
    public required int Rechecks { get; init; }

    /// <summary>Disagreements among them.</summary>
    public required int Disagreements { get; init; }

    /// <summary>How well the calibrated risk ranked mistakes on rechecks it was not fitted on; null when it could not be measured.</summary>
    public double? HeldOutRankAuc { get; init; }

    /// <summary>Why τ is what it is, or why the signal is not calibrated — for the snapshot's audit.</summary>
    public required string Reason { get; init; }
}
