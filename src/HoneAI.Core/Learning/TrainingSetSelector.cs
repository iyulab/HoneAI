using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Learning;

/// <summary>
/// How a training set is picked from the labels that stand — what a consumer otherwise writes as «the last N rows and a
/// class ratio». Each limit thins a group by the labels' hash order (independent of every answer, the same on every
/// site and every retry), so the same labels and policy give the same set.
/// </summary>
public sealed record TrainingSetPolicy
{
    /// <summary>
    /// At most this many labels per unit (<see cref="ConfirmedLabel{TValue}.Unit"/>, or the key the caller gives
    /// <see cref="TrainingSetSelector.Select{TValue}"/>): near-duplicates count for what they teach, once or a few times.
    /// Null: no limit.
    /// </summary>
    public int? MaxPerUnit { get; init; }

    /// <summary>At most this many labels per slice (a label without one is a slice of its own): a big slice does not take the set. Null: no limit.</summary>
    public int? MaxPerSlice { get; init; }

    /// <summary>
    /// At most this many labels in all. The labels are shared out between slices in proportion to what each has left,
    /// after <see cref="MinPerSlice"/> is met. Null: no limit.
    /// </summary>
    public int? MaxLabels { get; init; }

    /// <summary>
    /// Labels a slice keeps when <see cref="MaxLabels"/> forces a choice — a rare slice is not thinned below its floor
    /// (or below what it has). The floors together must fit in <see cref="MaxLabels"/>.
    /// </summary>
    public IReadOnlyDictionary<string, int>? MinPerSlice { get; init; }

    /// <summary>
    /// Whether the labels kept stand for those left out: each carries its keep probability (<c>s</c>) and its weight
    /// grows by 1/s, so a thinned group still counts for everything it had. Off — the default — a limit rebalances the
    /// set: a big slice or a run of near-duplicates counts for less than it had, which is usually why it was limited.
    /// </summary>
    public bool Compensate { get; init; }

    /// <summary>What the hash order is keyed with. Changing it changes which labels are kept.</summary>
    public string Salt { get; init; } = "selection";
}

/// <summary>A selected training set.</summary>
/// <param name="Labels">The labels kept, in decision order.</param>
/// <param name="Manifest">The set's manifest — with what was offered and what was left out, by reason.</param>
public sealed record TrainingSetSelection<TValue>(IReadOnlyList<ConfirmedLabel<TValue>> Labels, TrainingSetManifest Manifest);

/// <summary>
/// Picks a training set from the labels that stand: the one place that decides which confirmed labels are learned from
/// when there are more than should be. A pure function of the labels and the policy — HoneAI keeps no labels.
/// </summary>
public static class TrainingSetSelector
{
    private const string Unit = "unit", Slice = "slice", Budget = "budget";

    /// <summary>The training set <paramref name="policy"/> picks from <paramref name="labels"/>.</summary>
    /// <param name="labels">The labels that stand, one per decision, all for learning (<see cref="LabelShare.Training"/>).</param>
    /// <param name="policy">The limits.</param>
    /// <param name="split">The split the labels were confirmed under, for the manifest; null when nothing was held out.</param>
    /// <param name="unitOf">The unit of a label — a normalized input, a document; <see cref="ConfirmedLabel{TValue}.Unit"/> when null.</param>
    /// <param name="valueText">The value as text, for the manifest; its invariant text when null.</param>
    /// <exception cref="ArgumentException">A label is held out or two are for one decision (as <see cref="TrainingSets.Manifest{TValue}"/>), or a limit is not a count, or the floors do not fit in the budget.</exception>
    public static TrainingSetSelection<TValue> Select<TValue>(
        IEnumerable<ConfirmedLabel<TValue>> labels,
        TrainingSetPolicy policy,
        LabelSplit? split = null,
        Func<ConfirmedLabel<TValue>, string>? unitOf = null,
        Func<TValue, string>? valueText = null)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(policy);
        Validate(policy);
        unitOf ??= l => l.Unit;

        var offered = labels.ToList();
        // The same checks a manifest makes, before anything is dropped: a held-out label is not a candidate for learning.
        TrainingSets.Manifest(offered, split, valueText);

        var ordered = offered
            .OrderBy(l => RecheckDraw.Uniform(l.DecisionId, policy.Salt))
            .ThenBy(l => l.DecisionId, StringComparer.Ordinal)
            .Select(l => (Label: l, Keep: 1.0))
            .ToList();
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);

        if (policy.MaxPerUnit is { } perUnit)
            ordered = Thin(ordered, x => unitOf(x.Label), _ => perUnit, Unit, dropped);
        if (policy.MaxPerSlice is { } perSlice)
            ordered = Thin(ordered, x => x.Label.SliceKey ?? string.Empty, _ => perSlice, Slice, dropped);
        if (policy.MaxLabels is { } budget && ordered.Count > budget)
        {
            var share = Share(ordered.GroupBy(x => x.Label.SliceKey ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), budget, policy.MinPerSlice);
            ordered = Thin(ordered, x => x.Label.SliceKey ?? string.Empty, key => share[key], Budget, dropped);
        }

        var kept = ordered
            .Select(x => policy.Compensate && x.Keep < 1 ? x.Label with { SelectionProbability = x.Keep } : x.Label)
            .OrderBy(l => l.DecisionId, StringComparer.Ordinal)
            .ToList();
        var manifest = TrainingSets.Manifest(kept, split, valueText) with
        {
            Offered = offered.Count,
            Dropped = dropped,
            Compensated = policy.Compensate,
        };
        return new(kept, manifest);
    }

    // Keeps the first `limit(key)` of each group (the list is in hash order), and multiplies the keep probability of the
    // group's labels by limit/size — the chance each had of being kept.
    private static List<(ConfirmedLabel<TValue> Label, double Keep)> Thin<TValue>(
        List<(ConfirmedLabel<TValue> Label, double Keep)> ordered, Func<(ConfirmedLabel<TValue> Label, double Keep), string> keyOf,
        Func<string, int> limit, string reason, SortedDictionary<string, int> dropped)
    {
        var sizes = ordered.GroupBy(keyOf, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<(ConfirmedLabel<TValue>, double)>(ordered.Count);
        foreach (var x in ordered)
        {
            var key = keyOf(x);
            var n = sizes[key];
            var cap = limit(key);
            if (n <= cap)
            {
                kept.Add(x);
                continue;
            }
            var index = seen.GetValueOrDefault(key);
            seen[key] = index + 1;
            if (index < cap)
                kept.Add((x.Label, x.Keep * cap / n));
            else
                dropped[reason] = dropped.GetValueOrDefault(reason) + 1;
        }
        return kept;
    }

    // How many labels each slice keeps under a budget: its floor first, then the rest in proportion to what the slices
    // have left, by largest remainder (ties by slice key) so the total is the budget exactly.
    private static Dictionary<string, int> Share(Dictionary<string, int> sizes, int budget, IReadOnlyDictionary<string, int>? floors)
    {
        var share = sizes.ToDictionary(kv => kv.Key, kv => Math.Min(kv.Value, floors?.GetValueOrDefault(kv.Key) ?? 0), StringComparer.Ordinal);
        var floorTotal = share.Values.Sum();
        if (floorTotal > budget)
            throw new ArgumentException($"The slice floors keep {floorTotal} labels and the budget is {budget}.", nameof(floors));
        var spare = budget - floorTotal;
        var room = sizes.ToDictionary(kv => kv.Key, kv => kv.Value - share[kv.Key], StringComparer.Ordinal);
        var roomTotal = room.Values.Sum();
        if (roomTotal == 0)
            return share;

        var quotas = room.Select(kv => (Key: kv.Key, Exact: (double)spare * kv.Value / roomTotal)).ToList();
        foreach (var q in quotas)
            share[q.Key] += (int)Math.Floor(q.Exact);
        var left = budget - share.Values.Sum();
        foreach (var q in quotas
            .Where(q => share[q.Key] < sizes[q.Key])
            .OrderByDescending(q => q.Exact - Math.Floor(q.Exact))
            .ThenBy(q => q.Key, StringComparer.Ordinal)
            .Take(left))
            share[q.Key]++;
        return share;
    }

    private static void Validate(TrainingSetPolicy policy)
    {
        foreach (var (name, value) in new[] { (nameof(policy.MaxPerUnit), policy.MaxPerUnit), (nameof(policy.MaxPerSlice), policy.MaxPerSlice), (nameof(policy.MaxLabels), policy.MaxLabels) })
            if (value is < 1)
                throw new ArgumentException($"{name} is a count of at least 1; was {value}.", nameof(policy));
        if (policy.MinPerSlice?.Values.Any(v => v < 0) == true)
            throw new ArgumentException("A slice floor is a count of 0 or more.", nameof(policy));
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Salt, nameof(policy));
    }
}
