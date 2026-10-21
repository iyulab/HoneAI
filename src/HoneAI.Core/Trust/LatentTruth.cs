using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>One judge's answer to one item — the raw material of a latent-truth estimate.</summary>
/// <param name="ItemId">The item (a decision, a planted item).</param>
/// <param name="Judge">Who answered.</param>
/// <param name="Value">What it answered. One of the estimate's classes.</param>
[Experimental(LatentTruth.DiagnosticId)]
public sealed record JudgeLabel<TValue>(string ItemId, JudgeIdentity Judge, TValue Value);

/// <summary>Settings of <see cref="LatentTruth.Estimate{TValue}"/>.</summary>
[Experimental(LatentTruth.DiagnosticId)]
public sealed record LatentTruthOptions
{
    /// <summary>The most EM rounds. 200 by default.</summary>
    public int MaxIterations { get; init; } = 200;

    /// <summary>Stop once no item's posterior moves by more than this in a round. 1e-6 by default.</summary>
    public double Tolerance { get; init; } = 1e-6;

    /// <summary>
    /// Pseudo-count added to every cell of every confusion matrix and to the class prior, so a judge that never
    /// saw a class is not certain about it. 0.01 by default.
    /// </summary>
    public double Smoothing { get; init; } = 0.01;
}

/// <summary>An item's estimated truth.</summary>
[Experimental(LatentTruth.DiagnosticId)]
public sealed record LatentItem<TValue> where TValue : notnull
{
    public required string ItemId { get; init; }

    /// <summary>The probability of each class being the truth.</summary>
    public required IReadOnlyDictionary<TValue, double> Posterior { get; init; }

    /// <summary>The class with the highest posterior.</summary>
    public required TValue MostLikely { get; init; }

    /// <summary>The item's truth was given (a planted item, a consensus) — its posterior is that truth, not an estimate.</summary>
    public required bool Known { get; init; }
}

/// <summary>A judge's estimated behaviour.</summary>
[Experimental(LatentTruth.DiagnosticId)]
public sealed record LatentJudge<TValue> where TValue : notnull
{
    public required JudgeIdentity Judge { get; init; }

    /// <summary>Items the judge answered.</summary>
    public required int Items { get; init; }

    /// <summary>
    /// The confusion matrix: for each true class, the probability of each answer — <c>Confusion[truth][answer]</c>.
    /// A lean shows as mass off the diagonal in one direction (danger answered ok).
    /// </summary>
    public required IReadOnlyDictionary<TValue, IReadOnlyDictionary<TValue, double>> Confusion { get; init; }

    /// <summary>The probability the judge answers the truth, over the estimated class prior.</summary>
    public required double Accuracy { get; init; }
}

/// <summary>The joint estimate of the truth of every item and of every judge's confusion.</summary>
[Experimental(LatentTruth.DiagnosticId)]
public sealed record LatentTruthEstimate<TValue> where TValue : notnull
{
    /// <summary>The estimated share of each class among the items.</summary>
    public required IReadOnlyDictionary<TValue, double> Prior { get; init; }

    public required IReadOnlyList<LatentItem<TValue>> Items { get; init; }

    public required IReadOnlyList<LatentJudge<TValue>> Judges { get; init; }

    /// <summary>EM rounds run.</summary>
    public required int Iterations { get; init; }

    /// <summary>The posteriors stopped moving within <see cref="LatentTruthOptions.Tolerance"/> before the round limit.</summary>
    public required bool Converged { get; init; }
}

/// <summary>
/// Latent-truth estimation (method ⑦, Dawid–Skene): nobody's answer is taken as the truth — the truth of every
/// item and every judge's confusion matrix are estimated together, by expectation–maximisation, from who answered
/// what. Experimental: whether it beats the teacher-as-reference ledger is to be measured before it becomes the
/// ledger's default.
/// </summary>
/// <remarks>
/// It is identified only with three or more judges, or with items whose truth is known (planted items, consensus):
/// with two judges and nothing known, «both right» and «both wrong the same way» fit the answers equally, and the
/// estimate would report whichever the starting point chose. <see cref="Estimate{TValue}"/> refuses that case.
/// </remarks>
[Experimental(DiagnosticId)]
public static class LatentTruth
{
    /// <summary>The diagnostic a consumer suppresses to opt into this experimental surface.</summary>
    public const string DiagnosticId = "HONEAI001";

    /// <summary>
    /// The estimate from <paramref name="labels"/> over <paramref name="classes"/>, with the items in
    /// <paramref name="known"/> held at their known truth.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A label or a known truth outside <paramref name="classes"/>, fewer than two classes, or a judge answering one
    /// item twice.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Not identified: fewer than three judges and no known item.
    /// </exception>
    public static LatentTruthEstimate<TValue> Estimate<TValue>(
        IReadOnlyCollection<JudgeLabel<TValue>> labels,
        IReadOnlyList<TValue> classes,
        IReadOnlyDictionary<string, TValue>? known = null,
        LatentTruthOptions? options = null) where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(classes);
        options ??= new LatentTruthOptions();
        known ??= new Dictionary<string, TValue>();

        var classIndex = new Dictionary<TValue, int>();
        foreach (var c in classes)
            classIndex.TryAdd(c, classIndex.Count);
        var k = classIndex.Count;
        if (k < 2)
            throw new ArgumentException("An estimate needs two or more classes.", nameof(classes));

        int ClassOf(TValue value, string paramName) => classIndex.TryGetValue(value, out var i)
            ? i
            : throw new ArgumentException($"'{value}' is not one of the classes.", paramName);

        var itemIds = labels.Select(l => l.ItemId).Concat(known.Keys).Distinct().ToList();
        var itemIndex = itemIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var judges = labels.Select(l => l.Judge).Distinct().ToList();
        var judgeIndex = judges.Select((j, i) => (j, i)).ToDictionary(x => x.j, x => x.i);

        var answers = new List<(int Item, int Judge, int Answer)>(labels.Count);
        var seen = new HashSet<(int, int)>();
        foreach (var label in labels)
        {
            var entry = (itemIndex[label.ItemId], judgeIndex[label.Judge], ClassOf(label.Value, nameof(labels)));
            if (!seen.Add((entry.Item1, entry.Item2)))
                throw new ArgumentException($"'{label.Judge}' answered item '{label.ItemId}' twice.", nameof(labels));
            answers.Add(entry);
        }
        var clamped = known.ToDictionary(kv => itemIndex[kv.Key], kv => ClassOf(kv.Value, nameof(known)));

        if (judges.Count < 3 && clamped.Count == 0)
            throw new InvalidOperationException(
                $"Latent truth is not identified with {judges.Count} judge(s) and no item of known truth: «right together» "
                + "and «wrong the same way» fit the answers equally. Add a third judge or planted items.");

        var n = itemIds.Count;
        var byItem = answers.GroupBy(a => a.Item).ToDictionary(g => g.Key, g => g.ToList());

        // Start from the vote share on each item (uniform with no answer); known items are their truth.
        var t = new double[n, k];
        for (var i = 0; i < n; i++)
        {
            if (clamped.TryGetValue(i, out var truth))
            {
                t[i, truth] = 1;
                continue;
            }
            var votes = byItem.GetValueOrDefault(i);
            for (var c = 0; c < k; c++)
                t[i, c] = votes is null ? 1.0 / k : votes.Count(a => a.Answer == c) / (double)votes.Count;
        }

        var prior = new double[k];
        var confusion = new double[judges.Count, k, k];
        var iterations = 0;
        var converged = false;
        while (iterations < options.MaxIterations)
        {
            iterations++;

            // M-step: class prior and each judge's confusion from the current posteriors.
            for (var c = 0; c < k; c++)
            {
                double mass = 0;
                for (var i = 0; i < n; i++) mass += t[i, c];
                prior[c] = (mass + options.Smoothing) / (n + options.Smoothing * k);
            }
            Array.Clear(confusion);
            foreach (var (item, judge, answer) in answers)
                for (var c = 0; c < k; c++)
                    confusion[judge, c, answer] += t[item, c];
            for (var j = 0; j < judges.Count; j++)
                for (var c = 0; c < k; c++)
                {
                    double row = 0;
                    for (var a = 0; a < k; a++) row += confusion[j, c, a] += options.Smoothing;
                    for (var a = 0; a < k; a++) confusion[j, c, a] /= row;
                }

            // E-step: each unknown item's posterior from the prior and what its judges answered.
            double moved = 0;
            var log = new double[k];
            for (var i = 0; i < n; i++)
            {
                if (clamped.ContainsKey(i))
                    continue;
                for (var c = 0; c < k; c++)
                {
                    log[c] = Math.Log(prior[c]);
                    foreach (var (_, judge, answer) in byItem.GetValueOrDefault(i) ?? [])
                        log[c] += Math.Log(confusion[judge, c, answer]);
                }
                var max = log.Max();
                double sum = 0;
                for (var c = 0; c < k; c++) sum += Math.Exp(log[c] - max);
                for (var c = 0; c < k; c++)
                {
                    var next = Math.Exp(log[c] - max) / sum;
                    moved = Math.Max(moved, Math.Abs(next - t[i, c]));
                    t[i, c] = next;
                }
            }
            if (moved <= options.Tolerance)
            {
                converged = true;
                break;
            }
        }

        var classList = classIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        IReadOnlyDictionary<TValue, double> Row(Func<int, double> value)
            => classList.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => value(x.i));

        return new LatentTruthEstimate<TValue>
        {
            Prior = Row(c => prior[c]),
            Items = itemIds.Select((id, i) =>
            {
                var best = Enumerable.Range(0, k).MaxBy(c => t[i, c]);
                return new LatentItem<TValue>
                {
                    ItemId = id,
                    Posterior = Row(c => t[i, c]),
                    MostLikely = classList[best],
                    Known = clamped.ContainsKey(i),
                };
            }).ToList(),
            Judges = judges.Select((judge, j) => new LatentJudge<TValue>
            {
                Judge = judge,
                Items = answers.Count(a => a.Judge == j),
                Confusion = classList.Select((c, ci) => (c, ci))
                    .ToDictionary(x => x.c, x => Row(a => confusion[j, x.ci, a])),
                Accuracy = Enumerable.Range(0, k).Sum(c => prior[c] * confusion[j, c, c]),
            }).ToList(),
            Iterations = iterations,
            Converged = converged,
        };
    }
}
