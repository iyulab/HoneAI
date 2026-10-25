using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Learning;

/// <summary>What a retraining decision reads: what was learned since the last run, and what the measurements say.</summary>
public sealed record RetrainSignals
{
    /// <summary>When the learning tier last trained; null before its first training.</summary>
    public DateTimeOffset? LastTrainedAt { get; init; }

    /// <summary>A lifecycle run is under way and not settled — one at a time.</summary>
    public bool RunInFlight { get; init; }

    /// <summary>
    /// The effective sample of the learning labels confirmed since the last run (<see cref="EffectiveSample{TValue}"/>):
    /// how many evenly weighted labels they are worth.
    /// </summary>
    public double NewLabels { get; init; }

    /// <summary>The model's inputs or answers have moved (<c>StalenessWatch.Drifted</c>).</summary>
    public bool Stale { get; init; }

    /// <summary>The ledger tightened the model's version in some slice (<c>TrustLifecycle.Tighten</c> moved it down).</summary>
    public bool Demoted { get; init; }

    /// <summary>
    /// Kish's effective sample of <paramref name="labels"/>: (Σw)² / Σw². A thousand labels whose weights are mostly one
    /// decision's count for about one; evenly weighted, they count for their number. Held-out labels are not counted.
    /// </summary>
    public static double EffectiveSample<TValue>(IEnumerable<ConfirmedLabel<TValue>> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var weights = labels.Where(l => l.Share == LabelShare.Training).Select(l => l.Weight).ToList();
        var sum = weights.Sum();
        var squares = weights.Sum(w => w * w);
        return squares > 0 ? sum * sum / squares : 0;
    }
}

/// <summary>Whether to start a retraining run now, and why.</summary>
/// <param name="Start">Start one.</param>
/// <param name="Reason">Why, or why not — for the run's <see cref="LifecycleRequest.Trigger"/> or a log.</param>
public sealed record RetrainDecision(bool Start, string Reason);

/// <summary>
/// When a learning tier retrains — read from HoneAI's own measurements rather than a schedule: enough new labels, or
/// a model gone stale or demoted with something new to learn, never two runs at once, never closer than a minimum
/// interval. HoneAI answers; starting the run (<see cref="IModelLifecycle.StartAsync"/>) is the consumer's.
/// </summary>
public sealed record RetrainPolicy
{
    /// <summary>The effective sample of new labels that starts a run by itself.</summary>
    public required double MinimumNewLabels { get; init; }

    /// <summary>The least time between two runs — a stale or demoted model included, so a flapping signal does not retrain on every flap.</summary>
    public required TimeSpan MinimumInterval { get; init; }

    /// <summary>Whether a stale model retrains as soon as it has any new label. On by default.</summary>
    public bool OnStaleness { get; init; } = true;

    /// <summary>Whether a demoted model retrains as soon as it has any new label. On by default.</summary>
    public bool OnDemotion { get; init; } = true;

    /// <summary>Whether to start a run at <paramref name="now"/>.</summary>
    public RetrainDecision Decide(RetrainSignals signals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (!(MinimumNewLabels > 0))
            throw new InvalidOperationException("MinimumNewLabels is positive: a run needs something to learn from.");
        if (MinimumInterval < TimeSpan.Zero)
            throw new InvalidOperationException("MinimumInterval is not negative.");

        if (signals.RunInFlight)
            return new(false, "a run is under way");
        if (signals.LastTrainedAt is { } last && now - last < MinimumInterval)
            return new(false, $"last trained {now - last:d\\.hh\\:mm} ago, under the minimum interval {MinimumInterval:d\\.hh\\:mm}");

        var labels = $"{signals.NewLabels:0.#} new labels (effective)";
        if (signals.NewLabels >= MinimumNewLabels)
            return new(true, $"{labels}, at least {MinimumNewLabels:0.#}");
        if (signals.NewLabels > 0 && OnDemotion && signals.Demoted)
            return new(true, $"the model was demoted, and there are {labels}");
        if (signals.NewLabels > 0 && OnStaleness && signals.Stale)
            return new(true, $"the model's inputs or answers moved, and there are {labels}");
        if (signals.NewLabels <= 0 && (signals.Demoted || signals.Stale))
            return new(false, "the model is " + (signals.Demoted ? "demoted" : "stale") + ", but nothing new was confirmed to learn from");
        return new(false, $"{labels}, under {MinimumNewLabels:0.#}");
    }
}
