using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>How a judge is asked: the measurement methods that share one selection policy.</summary>
public enum ProbeMethod
{
    /// <summary>The tier above judges a settled decision again, blind (the recheck).</summary>
    Recheck = 0,

    /// <summary>Two judges of the same depth judge the same item, blind to each other.</summary>
    DoubleCheck = 1,

    /// <summary>The same judge is asked the same item again.</summary>
    Repeat = 2,

    /// <summary>An item whose answer is known is slipped in among the real ones.</summary>
    Plant = 3,

    /// <summary>A judge is shown another's answer, to measure how far it follows.</summary>
    Anchoring = 4,
}

/// <summary>
/// An item that could be put to a judge by a method: what to ask, whom, how, and how much asking it is worth. The value
/// is the consumer's — risk calibrated by the ledger, disagreement between tiers, a new slice, the volume it touches,
/// the exposure to missed must-catch values, time since the judge was last measured, a version that just changed — and
/// the selector only ranks by it, so it knows nothing of a domain or of the kind of judge asked.
/// </summary>
public sealed record ProbeCandidate
{
    /// <summary>The item. The draw is a hash of it (with the method and the judge), fixed by nothing a judge answered.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The slice, for the record.</summary>
    public required string SliceKey { get; init; }

    /// <summary>Whom to ask — a tier, or a person within a rung of people.</summary>
    public required JudgeIdentity Target { get; init; }

    /// <summary>How.</summary>
    public required ProbeMethod Method { get; init; }

    /// <summary>The pool of capacity the probe is paid from (a tenant's share, a person's hours, a day's cost).</summary>
    public required string Pool { get; init; }

    /// <summary>
    /// What asking it is worth, ≥ 0, on any scale: probabilities are proportional to it within a pool and method. All
    /// zero makes every candidate equally worth asking.
    /// </summary>
    public double Value { get; init; } = 1;
}

/// <summary>How many probes each pool pays for, and how the pool is shared between methods.</summary>
public sealed record ProbePolicy
{
    /// <summary>Probes per window each pool pays for, in expectation — a pool without a budget is refused.</summary>
    public required IReadOnlyDictionary<string, double> PoolBudgets { get; init; }

    /// <summary>
    /// The share of a pool's budget each method gets, each in [0, 1] and summing to at most 1. Null: the methods that
    /// have candidates in the pool share it equally. A method with no share has nothing to spend, and a candidate of it
    /// is refused rather than given probability zero.
    /// </summary>
    public IReadOnlyDictionary<ProbeMethod, double>? MethodShares { get; init; }

    /// <summary>
    /// The least probability any candidate has, in (0, 1]: an item is never certain to be left unasked, so the
    /// measurement of what is not prioritized stays unbiased (a probability of zero cannot be weighted back up).
    /// Taken from the budget first, down to the budget's share per candidate.
    /// </summary>
    public double MinimumProbability { get; init; } = 0.01;
}

/// <summary>
/// One probe's selection: the probability it was drawn at and the draw. The probability is the method's own — a
/// ledger counts each method on its own denominator (<see cref="ProbeMethod"/>), never one pooled across methods whose
/// blinding and noise differ.
/// </summary>
/// <param name="Candidate">What was put forward.</param>
/// <param name="Probability">π — the chance it had of being drawn, in (0, 1].</param>
/// <param name="Drawn">Whether it was drawn: a uniform number fixed by the item, the method, the judge and the plan version is under π.</param>
public sealed record Probe(ProbeCandidate Candidate, double Probability, bool Drawn);

/// <summary>A selection: every candidate with its probability and draw, and what each (pool, method) was expected to spend.</summary>
/// <param name="Version">The plan's version — what the draws are keyed with.</param>
/// <param name="Probes">All candidates, in the order given.</param>
/// <param name="Expected">The sum of probabilities per pool and method — the expected number of probes, within the budget.</param>
public sealed record ProbePlan(string Version, IReadOnlyList<Probe> Probes, IReadOnlyDictionary<(string Pool, ProbeMethod Method), double> Expected)
{
    /// <summary>The probes to run.</summary>
    public IEnumerable<Probe> Drawn => Probes.Where(p => p.Drawn);
}

/// <summary>
/// Chooses what to ask whom and how — one policy for every kind of judge and every method. The kind of judge changes how
/// a probe is put (a call, a lookup, a card in a person's queue), never which probe is chosen.
/// </summary>
/// <remarks>
/// A pure function of the candidates and the policy: within each pool, the budget is shared between methods, and within a
/// method the probabilities are proportional to the candidates' values, capped at 1 with the excess spread over the
/// rest, after every candidate has its floor. The draw is a hash, so any site replays it, and the priority enters only
/// through π — which is what keeps the measurement unbiased when it is weighted by 1/π.
/// </remarks>
public static class ProbeSelector
{
    /// <summary>Selects among <paramref name="candidates"/> for the plan <paramref name="version"/>.</summary>
    /// <exception cref="ArgumentException">A candidate in a pool without a budget, or of a method with no share of it, a value that is negative or not a number, two candidates for one item, judge and method, or shares that exceed 1.</exception>
    public static ProbePlan Select(IReadOnlyList<ProbeCandidate> candidates, ProbePolicy policy, string version)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!(policy.MinimumProbability > 0 && policy.MinimumProbability <= 1))
            throw new ArgumentOutOfRangeException(nameof(policy), policy.MinimumProbability, "The minimum probability lies in (0, 1].");
        if (policy.MethodShares is { } shares && (shares.Values.Any(s => !(s >= 0 && s <= 1)) || shares.Values.Sum() > 1 + 1e-9))
            throw new ArgumentException("Method shares are each in [0, 1] and sum to at most 1.", nameof(policy));
        foreach (var c in candidates)
        {
            ArgumentNullException.ThrowIfNull(c, nameof(candidates));
            if (!(c.Value >= 0) || double.IsInfinity(c.Value))
                throw new ArgumentException($"The value of '{c.DecisionId}' is {c.Value}; a value is a finite number of 0 or more.", nameof(candidates));
            if (!policy.PoolBudgets.ContainsKey(c.Pool))
                throw new ArgumentException($"Pool '{c.Pool}' has no budget.", nameof(candidates));
        }
        if (candidates.GroupBy(c => (c.DecisionId, c.Target, c.Method)).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new ArgumentException($"'{twice.Key.DecisionId}' is put to {twice.Key.Target} by {twice.Key.Method} twice.", nameof(candidates));

        var probabilities = new double[candidates.Count];
        var expected = new Dictionary<(string, ProbeMethod), double>();
        foreach (var pool in candidates.Select((c, i) => (c, i)).GroupBy(x => x.c.Pool, StringComparer.Ordinal))
        {
            var methods = pool.GroupBy(x => x.c.Method).ToList();
            foreach (var group in methods)
            {
                var budget = policy.PoolBudgets[pool.Key] * (policy.MethodShares is { } s
                    ? s.GetValueOrDefault(group.Key)
                    : 1.0 / methods.Count);
                var items = group.ToList();
                if (!(budget > 0))
                    throw new ArgumentException(
                        $"{group.Key} has no budget in pool '{pool.Key}'; a candidate that cannot be drawn cannot be weighted either.", nameof(candidates));
                var allocated = Allocate(items.Select(x => x.c.Value).ToList(), budget, policy.MinimumProbability);
                for (var k = 0; k < items.Count; k++)
                    probabilities[items[k].i] = allocated[k];
                expected[(pool.Key, group.Key)] = allocated.Sum();
            }
        }

        var probes = candidates
            .Select((c, i) => new Probe(c, probabilities[i],
                RecheckDraw.Uniform($"{c.DecisionId}\n{c.Method}\n{c.Target}", version) < probabilities[i]))
            .ToList();
        return new ProbePlan(version, probes, expected);
    }

    // The floor for every candidate first (down to what the budget affords), then the rest of the budget in proportion to
    // the values, with anyone who would pass 1 held at 1 and the excess handed to the others.
    internal static double[] Allocate(IReadOnlyList<double> values, double budget, double floor)
    {
        var n = values.Count;
        var p = new double[n];
        if (n == 0)
            return p;
        var spend = Math.Min(budget, n);
        if (spend >= n)
            return Enumerable.Repeat(1.0, n).ToArray();
        var @base = Math.Min(floor, spend / n);
        Array.Fill(p, @base);
        var remaining = spend - @base * n;

        var open = Enumerable.Range(0, n).ToList();
        while (remaining > 1e-12 && open.Count > 0)
        {
            var total = open.Sum(i => values[i]);
            var weights = total > 0 ? open.Select(i => values[i] / total).ToList() : open.Select(_ => 1.0 / open.Count).ToList();
            var capped = new List<int>();
            for (var k = 0; k < open.Count; k++)
                if (p[open[k]] + remaining * weights[k] >= 1)
                    capped.Add(open[k]);
            if (capped.Count == 0)
            {
                for (var k = 0; k < open.Count; k++)
                    p[open[k]] += remaining * weights[k];
                break;
            }
            foreach (var i in capped)
            {
                remaining -= 1 - p[i];
                p[i] = 1;
            }
            open.RemoveAll(capped.Contains);
        }
        return p;
    }
}
