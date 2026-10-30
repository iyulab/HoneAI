using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>
/// One window of <see cref="UpperTierShare.Curve"/>: how many of the window's decisions reached a tier above the first, and how many
/// were drawn for a recheck by the tier above the one that settled them.
/// </summary>
/// <param name="Start">The window's start (inclusive).</param>
/// <param name="End">The window's end (exclusive).</param>
/// <param name="Decisions">Decisions recorded in the window (provenance with a path).</param>
/// <param name="Escalated">Decisions whose path went above the first tier — it abstained, threw, or could not settle.</param>
/// <param name="Rechecked">Decisions the first tier settled that were drawn for a recheck.</param>
/// <param name="Calls">Calls to tiers above the first: every extra step on a path, plus one for each drawn recheck.</param>
public sealed record UpperTierShareWindow(DateTimeOffset Start, DateTimeOffset End, int Decisions, int Escalated, int Rechecked, int Calls)
{
    /// <summary>The share of decisions that cost an upper-tier call, by escalation or by recheck.</summary>
    public double Share => Decisions == 0 ? 0 : (double)(Escalated + Rechecked) / Decisions;

    /// <summary>Upper-tier calls per decision (more than <see cref="Share"/> where a decision climbed through several tiers).</summary>
    public double CallsPerDecision => Decisions == 0 ? 0 : (double)Calls / Decisions;
}

/// <summary>
/// The share of decisions that reach the tiers above the first, window by window — read from the recorded provenance, not worked out
/// from the trust ledger's policy. It is a report, not a target: standing is earned slice by slice from evidence, a new slice or a
/// new version of a tier climbs back up, so the curve falls as the first tier earns the work and rises when it loses it.
/// </summary>
public static class UpperTierShare
{
    /// <summary>
    /// The share per window of <paramref name="window"/> length, counted from <paramref name="origin"/>, over the records that carry a
    /// path, in time order; a window with no decision is left out. A decision costs an upper-tier call when its path has a step
    /// beyond the first (<see cref="UpperTierShareWindow.Escalated"/>), or — when the first tier settled it — when it was drawn for a
    /// recheck (<see cref="UpperTierShareWindow.Rechecked"/>). A shadow answer is a call too: it is a step on the path.
    /// </summary>
    /// <param name="records">The recorded provenance (<see cref="IProvenanceSink.ReadAsync"/>).</param>
    /// <param name="window">The window's length (a week, in the design's curve).</param>
    /// <param name="origin">Where the first window starts; records before it are left out.</param>
    /// <param name="sliceKey">When given, only the decisions of that slice.</param>
    public static IReadOnlyList<UpperTierShareWindow> Curve(
        IEnumerable<ProvenanceRecord> records, TimeSpan window, DateTimeOffset origin, string? sliceKey = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "A window has a positive length.");

        return records
            .Where(r => r.RecordedAt >= origin && r.Provenance.Path is { Count: > 0 }
                && (sliceKey is null || string.Equals(r.Provenance.SliceKey, sliceKey, StringComparison.Ordinal)))
            .GroupBy(r => (r.RecordedAt - origin).Ticks / window.Ticks)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var start = origin + TimeSpan.FromTicks(g.Key * window.Ticks);
                var escalated = g.Count(r => r.Provenance.Path!.Count > 1);
                var rechecked = g.Count(r => r.Provenance.Path!.Count == 1 && r.Provenance.Sample is { Drawn: true });
                var calls = g.Sum(r => r.Provenance.Path!.Count - 1 + (r.Provenance.Sample is { Drawn: true } ? 1 : 0));
                return new UpperTierShareWindow(start, start + window, g.Count(), escalated, rechecked, calls);
            })
            .ToList();
    }
}
