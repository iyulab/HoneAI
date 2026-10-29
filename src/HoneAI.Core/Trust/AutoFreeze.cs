using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>What happened to a slice's tier that a hold may be placed for.</summary>
public enum StandingEventKind
{
    /// <summary>Production was put back to what a version replaced (<see cref="RollbackRule"/>).</summary>
    RolledBack = 0,

    /// <summary>The tier's version changed (<see cref="LiveSignals.TierVersion"/> no longer matches the standing).</summary>
    VersionChanged = 1,

    /// <summary>The tier earned a release (shadow → active or active → stable) that took effect.</summary>
    Released = 2,
}

/// <summary>One thing that happened to a slice's tier, and when.</summary>
public sealed record StandingEvent(DateTimeOffset At, StandingEventKind Kind);

/// <summary>
/// When a hold is placed without a person. Every number is a person's: how many rollbacks in a row, how many version changes,
/// over how long, and how much rechecking a stable slice keeps while held.
/// </summary>
/// <param name="ConsecutiveRollbacks">Rollbacks with no release between them that place a hold; 0 turns this reason off.</param>
/// <param name="VersionChanges">Version changes that place a hold; 0 turns this reason off.</param>
/// <param name="Within">The time the events are counted over, ending at the moment asked.</param>
/// <param name="MinimumRate">The recheck rate a held stable slice keeps (<see cref="SliceFreeze.MinimumRate"/>); null leaves its grant as it comes.</param>
public sealed record AutoFreezePolicy(int ConsecutiveRollbacks, int VersionChanges, TimeSpan Within, double? MinimumRate = null);

/// <summary>
/// Places the <see cref="SliceFreeze"/> overlay on a slice whose tier keeps being put back or replaced — the automatic
/// reasons for the hold a person can also place. The hold stops what loosens (a release, a lower recheck rate)
/// and never what tightens, so it cannot make a slice less safe; a person lifts it.
/// </summary>
/// <remarks>
/// Pure: it keeps nothing between calls. The caller keeps the slice's events (a rollback it applied, a version it saw change,
/// a release it committed) and asks at each window's end; the answer is the hold to set on <see cref="SliceWindow.Freeze"/>,
/// or <c>null</c>. A slice already held is the caller's to skip — a person's hold stands as it is.
/// </remarks>
public static class AutoFreeze
{
    /// <summary>The <see cref="SliceFreeze.By"/> of a hold placed here.</summary>
    public const string By = "auto";

    /// <summary>The hold the events of the slice call for at <paramref name="now"/> under <paramref name="policy"/>, or <c>null</c>.</summary>
    public static SliceFreeze? Decide(IEnumerable<StandingEvent> events, AutoFreezePolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.ConsecutiveRollbacks < 0 || policy.VersionChanges < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Counts cannot be negative.");
        if (policy.Within <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Within, "The time events are counted over must be positive.");
        if (policy.MinimumRate is { } rate && !(rate > 0 && rate <= 1))
            throw new ArgumentOutOfRangeException(nameof(policy), rate, "A rate lies in (0, 1].");

        var since = now - policy.Within;
        var recent = events.Where(e => e.At > since && e.At <= now).OrderBy(e => e.At).ToList();

        if (policy.ConsecutiveRollbacks > 0)
        {
            var run = 0;
            for (var i = recent.Count - 1; i >= 0 && recent[i].Kind != StandingEventKind.Released; i--)
                if (recent[i].Kind == StandingEventKind.RolledBack)
                    run++;
            if (run >= policy.ConsecutiveRollbacks)
                return new SliceFreeze(By, now, $"{run} rollbacks with no release between them in {Describe(policy.Within)}", policy.MinimumRate);
        }

        if (policy.VersionChanges > 0)
        {
            var changes = recent.Count(e => e.Kind == StandingEventKind.VersionChanged);
            if (changes >= policy.VersionChanges)
                return new SliceFreeze(By, now, $"the tier's version changed {changes} times in {Describe(policy.Within)}", policy.MinimumRate);
        }

        return null;
    }

    private static string Describe(TimeSpan span) => span.TotalDays >= 1 && span.TotalDays % 1 == 0 ? $"{span.TotalDays:0} days" : $"{span.TotalHours:0.#} hours";
}
