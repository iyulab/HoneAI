using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;
using HoneAI.Trust;

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

    /// <summary>
    /// With a priority (<c>priority</c> of <see cref="TrainingSetSelector.Select{TValue}"/>), the share of each group's
    /// keep probability that ignores it, in [0, 1] — every label keeps a chance (at least this share of an even one), so a
    /// signal that ranks badly costs little and the labels it never favors still count. Without a priority nothing is mixed.
    /// </summary>
    public double PriorityUniformMix { get; init; } = 0.1;

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
    /// <param name="priority">
    /// How much a label is worth keeping when a limit forces a choice, relative to an ordinary one (1) — finite and ≥ 0.
    /// Within a group that has to be thinned, a label is kept with a probability proportional to it (a fixed-size,
    /// systematic draw in hash order, so the same labels and policy still give the same set), mixed with an even chance by
    /// <see cref="TrainingSetPolicy.PriorityUniformMix"/>. It is recorded as the label's keep probability, so with
    /// <see cref="TrainingSetPolicy.Compensate"/> the labels kept still stand for the ones left out. Null: every label
    /// has the same chance. <see cref="PriorityFrom{TValue}"/> takes it from a selection fitted for the recheck.
    /// </param>
    /// <exception cref="ArgumentException">A label is held out or two are for one decision (as <see cref="TrainingSets.Manifest{TValue}"/>), or a limit is not a count, or the floors do not fit in the budget.</exception>
    public static TrainingSetSelection<TValue> Select<TValue>(
        IEnumerable<ConfirmedLabel<TValue>> labels,
        TrainingSetPolicy policy,
        LabelSplit? split = null,
        Func<ConfirmedLabel<TValue>, string>? unitOf = null,
        Func<TValue, string>? valueText = null,
        Func<ConfirmedLabel<TValue>, double>? priority = null)
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
            ordered = Thin(ordered, x => unitOf(x.Label), _ => perUnit, Unit, dropped, priority, policy);
        if (policy.MaxPerSlice is { } perSlice)
            ordered = Thin(ordered, x => x.Label.SliceKey ?? string.Empty, _ => perSlice, Slice, dropped, priority, policy);
        if (policy.MaxLabels is { } budget && ordered.Count > budget)
        {
            var share = Share(ordered.GroupBy(x => x.Label.SliceKey ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), budget, policy.MinPerSlice);
            ordered = Thin(ordered, x => x.Label.SliceKey ?? string.Empty, key => share[key], Budget, dropped, priority, policy);
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
            Prioritized = priority is not null,
        };
        return new(kept, manifest);
    }

    // Keeps `limit(key)` of each group and multiplies the keep probability of the group's labels by the chance each had of
    // being kept. Without a priority that is the first `limit` in hash order, limit/size each. With one, the labels are kept
    // by a fixed-size systematic draw in hash order whose inclusion probabilities follow the priority (InclusionProbabilities),
    // each label's own probability.
    private static List<(ConfirmedLabel<TValue> Label, double Keep)> Thin<TValue>(
        List<(ConfirmedLabel<TValue> Label, double Keep)> ordered, Func<(ConfirmedLabel<TValue> Label, double Keep), string> keyOf,
        Func<string, int> limit, string reason, SortedDictionary<string, int> dropped,
        Func<ConfirmedLabel<TValue>, double>? priority, TrainingSetPolicy policy)
    {
        var groups = ordered.GroupBy(keyOf, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // key -> the chance of each member (in list order); 0 = left out. Absent: the group fits and keeps everyone.
        var chances = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (key, members) in groups)
        {
            var cap = limit(key);
            if (members.Count <= cap)
                continue;
            if (priority is null)
            {
                chances[key] = Enumerable.Range(0, members.Count).Select(i => i < cap ? (double)cap / members.Count : 0.0).ToArray();
                continue;
            }
            var weights = members.Select(m => PriorityOf(priority, m.Label)).ToList();
            var inclusion = InclusionProbabilities(weights, cap, policy.PriorityUniformMix);
            chances[key] = SystematicDraw(inclusion, RecheckDraw.Uniform(key, policy.Salt + "/" + reason));
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<(ConfirmedLabel<TValue>, double)>(ordered.Count);
        foreach (var x in ordered)
        {
            var key = keyOf(x);
            if (!chances.TryGetValue(key, out var chance))
            {
                kept.Add(x);
                continue;
            }
            var index = seen.GetValueOrDefault(key);
            seen[key] = index + 1;
            if (chance[index] > 0)
                kept.Add((x.Label, x.Keep * chance[index]));
            else
                dropped[reason] = dropped.GetValueOrDefault(reason) + 1;
        }
        return kept;
    }

    private static double PriorityOf<TValue>(Func<ConfirmedLabel<TValue>, double> priority, ConfirmedLabel<TValue> label)
    {
        var value = priority(label);
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentException($"The priority of decision '{label.DecisionId}' is {value}; a priority is finite and at least 0.", nameof(priority));
        return value;
    }

    /// <summary>
    /// A selection's priority for a training label: <see cref="SelectionScore.Priority"/> of the signal
    /// <paramref name="signalOf"/> gives the label's decision — the signal the recheck was fitted on (<see cref="SelectionFit"/>),
    /// used for the same purpose: the decisions most likely wrong are the ones most worth learning from. A label without a signal,
    /// or a selection not calibrated yet, is ordinary (1).
    /// </summary>
    /// <param name="selection">The knob's fitted selection.</param>
    /// <param name="signalOf">The selection signal of a label's decision, or null when there is none.</param>
    public static Func<ConfirmedLabel<TValue>, double> PriorityFrom<TValue>(
        KnobSelection selection, Func<ConfirmedLabel<TValue>, SelectionSignal?> signalOf)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(signalOf);
        return label => SelectionScore.Priority(selection, signalOf(label)) ?? 1.0;
    }

    // Probabilities in (0, 1] that sum to `k`, proportional to the priorities mixed with an even share (`mix`). A probability
    // above 1 is held at 1 — one at a time, the largest first — and the rest share what is left, so at most k − 1 are held
    // and every other label keeps a positive chance.
    internal static double[] InclusionProbabilities(IReadOnlyList<double> priorities, int k, double mix)
    {
        var n = priorities.Count;
        var total = priorities.Sum();
        var weights = priorities.Select(p => (1 - mix) * (total > 0 ? p / total : 1.0 / n) + mix / n).ToArray();
        var probabilities = new double[n];
        var held = new bool[n];
        var remaining = (double)k;
        while (true)
        {
            var freeWeight = Enumerable.Range(0, n).Where(i => !held[i]).Sum(i => weights[i]);
            var worst = -1;
            for (var i = 0; i < n; i++)
            {
                if (held[i])
                    continue;
                probabilities[i] = remaining * weights[i] / freeWeight;
                if (probabilities[i] > 1 && (worst < 0 || probabilities[i] > probabilities[worst]))
                    worst = i;
            }
            if (worst < 0)
                break;
            held[worst] = true;
            probabilities[worst] = 1;
            remaining -= 1;
        }
        return probabilities;
    }

    // A fixed-size draw with the given inclusion probabilities (summing to an integer): one random start, then every unit step
    // along the running sum picks the label it lands in. Returns each label's probability when picked, 0 when not.
    internal static double[] SystematicDraw(double[] inclusion, double start)
    {
        var picked = new double[inclusion.Length];
        double before = 0;
        for (var i = 0; i < inclusion.Length; i++)
        {
            var after = before + inclusion[i];
            var from = Math.Max(0, Math.Ceiling(before - start - 1e-9));
            var to = Math.Max(0, Math.Ceiling(after - start - 1e-9));
            if (to > from)
                picked[i] = inclusion[i];
            before = after;
        }
        return picked;
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
        if (!(policy.PriorityUniformMix >= 0 && policy.PriorityUniformMix <= 1))
            throw new ArgumentException($"The priority's uniform mix is in [0, 1]; was {policy.PriorityUniformMix}.", nameof(policy));
    }
}
