using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>A slice's agreement estimate after borrowing strength from its group and from every slice.</summary>
public sealed record ShrunkAgreement
{
    /// <summary>The tier.</summary>
    public required string TierId { get; init; }

    /// <summary>The slice.</summary>
    public required string SliceKey { get; init; }

    /// <summary>The slice's group (<c>groupOf</c>); null for a slice that sits directly under the whole.</summary>
    public string? Group { get; init; }

    /// <summary>The slice's own agreement (<see cref="LedgerCell.Agreement"/>). Null when nothing was answered.</summary>
    public double? Raw { get; init; }

    /// <summary>The slice's own effective sample size — how much its own evidence weighs.</summary>
    public required double EffectiveSampleSize { get; init; }

    /// <summary>
    /// What the slice is pulled toward: its group's shrunk agreement, or the whole's for a slice without a group.
    /// Null when no slice anywhere has an answered recheck.
    /// </summary>
    public double? Toward { get; init; }

    /// <summary>
    /// How many rechecks' worth the level above counts for: 0 when slices differ so much the parent says nothing
    /// about them (the raw estimate stands), infinite when they do not differ beyond sampling noise (the parent
    /// stands). Estimated from the spread of the slices themselves.
    /// </summary>
    public required double PriorStrength { get; init; }

    /// <summary>
    /// The shrunk agreement: (n · raw + κ · toward) / (n + κ). With nothing answered, the level above; null when
    /// neither the slice nor any level above has evidence.
    /// </summary>
    public double? Agreement { get; init; }
}

/// <summary>
/// Hierarchical shrinkage of agreement (slice → group → whole): a slice with few rechecks borrows strength from
/// the slices like it, as far as those slices are alike — empirical Bayes, with the between-slice spread
/// estimated by the method of moments at each level.
/// </summary>
/// <remarks>
/// The shrunk estimate is for ordering and reporting — which small slice looks worst, what a cold slice probably
/// is. It is never what a slice gains ground on: a release rests on the slice's own rechecks
/// (<see cref="TrustLifecycle"/>), since a slice that hides a rare danger would otherwise ride on its siblings.
/// </remarks>
public static class LedgerShrinkage
{
    /// <summary>
    /// The shrunk agreement of every cell in <paramref name="cells"/> (one tier, one window), grouped by
    /// <paramref name="groupOf"/> — a slice's group, or null for none.
    /// </summary>
    /// <exception cref="ArgumentException">The cells are of more than one tier, or a slice appears twice.</exception>
    public static IReadOnlyList<ShrunkAgreement> Agreement(IReadOnlyCollection<LedgerCell> cells, Func<string, string?> groupOf)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(groupOf);
        if (cells.Count == 0)
            return [];
        if (cells.Select(c => c.TierId).Distinct().Count() > 1)
            throw new ArgumentException("Shrinkage pools the slices of one tier.", nameof(cells));
        if (cells.Select(c => c.SliceKey).Distinct().Count() != cells.Count)
            throw new ArgumentException("Each slice appears once.", nameof(cells));

        var units = cells
            .Select(c => (Cell: c, Group: groupOf(c.SliceKey), N: c.Agreement is null ? 0 : c.EffectiveSampleSize))
            .ToList();
        var whole = Pooled(units.Select(u => (u.Cell.Agreement, u.N)));

        // Groups toward the whole, as one level; slices without a group sit at that level too.
        var groups = units.Where(u => u.Group is not null).GroupBy(u => u.Group!)
            .Select(g => (Key: g.Key, P: Pooled(g.Select(u => (u.Cell.Agreement, u.N))), N: g.Sum(u => u.N)))
            .ToList();
        var topLevel = groups.Select(g => (g.P, g.N))
            .Concat(units.Where(u => u.Group is null).Select(u => (u.Cell.Agreement, u.N)))
            .ToList();
        var topStrength = whole is { } w ? PriorStrength(topLevel, w) : 0;
        var groupShrunk = groups.ToDictionary(g => g.Key, g => Shrink(g.P, g.N, whole, topStrength));

        return units.Select(u =>
        {
            double? toward;
            double strength;
            if (u.Group is null)
                (toward, strength) = (whole, topStrength);
            else
            {
                toward = groupShrunk[u.Group];
                strength = toward is { } t
                    ? PriorStrength(units.Where(v => v.Group == u.Group).Select(v => (v.Cell.Agreement, v.N)).ToList(), t)
                    : 0;
            }
            return new ShrunkAgreement
            {
                TierId = u.Cell.TierId,
                SliceKey = u.Cell.SliceKey,
                Group = u.Group,
                Raw = u.Cell.Agreement,
                EffectiveSampleSize = u.N,
                Toward = toward,
                PriorStrength = strength,
                Agreement = Shrink(u.Cell.Agreement, u.N, toward, strength),
            };
        }).ToList();
    }

    /// <summary>
    /// κ for units estimating proportions around <paramref name="mean"/>: μ(1 − μ)/τ² − 1, with the between-unit
    /// variance τ² by the method of moments, Σnᵢ(pᵢ − μ)² ≈ (k − 1)μ(1 − μ) + τ²(N − Σnᵢ²/N). 0 with fewer than two
    /// units holding evidence (nothing to pool over); infinite when the spread is within sampling noise.
    /// </summary>
    public static double PriorStrength(IReadOnlyCollection<(double? P, double N)> units, double mean)
    {
        ArgumentNullException.ThrowIfNull(units);
        var observed = units.Where(u => u.P is not null && u.N > 0).Select(u => (P: u.P!.Value, u.N)).ToList();
        if (observed.Count < 2)
            return 0;
        var total = observed.Sum(u => u.N);
        var spread = observed.Sum(u => u.N * (u.P - mean) * (u.P - mean));
        var noise = mean * (1 - mean);
        var denominator = total - observed.Sum(u => u.N * u.N) / total;
        if (noise <= 0 || denominator <= 0)
            return double.PositiveInfinity;
        var between = (spread - (observed.Count - 1) * noise) / denominator;
        if (between <= 0)
            return double.PositiveInfinity;
        return Math.Max(0, noise / between - 1);
    }

    private static double? Pooled(IEnumerable<(double? P, double N)> units)
    {
        var observed = units.Where(u => u.P is not null && u.N > 0).ToList();
        var n = observed.Sum(u => u.N);
        return n > 0 ? observed.Sum(u => u.P!.Value * u.N) / n : null;
    }

    private static double? Shrink(double? raw, double n, double? toward, double strength)
    {
        if (raw is null || n <= 0)
            return toward;
        if (toward is not { } t)
            return raw;
        if (double.IsPositiveInfinity(strength))
            return t;
        return (n * raw.Value + strength * t) / (n + strength);
    }
}
