using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HoneAI.Learning;

/// <summary>What changed between two training sets — label counts that differ, by category; zero changes are left out.</summary>
/// <param name="Labels">Labels in the later set minus the earlier.</param>
/// <param name="BySource">Count changes by confirming judge.</param>
/// <param name="ByTrustBasis">Count changes by trust basis.</param>
/// <param name="BySlice">Count changes by slice.</param>
/// <param name="ByValue">Count changes by value.</param>
/// <param name="SplitChanged">The held-out split differs — the two sets were not drawn from the same decisions.</param>
public sealed record TrainingSetChange(
    int Labels,
    IReadOnlyDictionary<string, int> BySource,
    IReadOnlyDictionary<string, int> ByTrustBasis,
    IReadOnlyDictionary<string, int> BySlice,
    IReadOnlyDictionary<string, int> ByValue,
    bool SplitChanged);

/// <summary>Describes and compares the sets of confirmed labels a learning tier is trained on.</summary>
public static class TrainingSets
{
    /// <summary>The manifest of <paramref name="labels"/> — the labels one training run learns from.</summary>
    /// <param name="labels">The labels, as the consumer kept them. All for learning: a held-out label is refused.</param>
    /// <param name="split">The split they were confirmed under; null when nothing was held out.</param>
    /// <param name="valueText">The value as text; its invariant text when null.</param>
    /// <exception cref="ArgumentException">A label is held out (<see cref="LabelShare.Evaluation"/>), or two are for one decision.</exception>
    public static TrainingSetManifest Manifest<TValue>(
        IEnumerable<ConfirmedLabel<TValue>> labels, LabelSplit? split = null, Func<TValue, string>? valueText = null)
    {
        ArgumentNullException.ThrowIfNull(labels);
        valueText ??= v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
        var list = labels.OrderBy(l => l.DecisionId, StringComparer.Ordinal).ToList();
        foreach (var label in list)
        {
            ArgumentNullException.ThrowIfNull(label, nameof(labels));
            if (label.Share != LabelShare.Training)
                throw new ArgumentException(
                    $"Decision '{label.DecisionId}' is held out for measuring the learner; it is not part of a training set.", nameof(labels));
        }
        var repeated = list.GroupBy(l => l.DecisionId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw new ArgumentException(
                $"Decision '{repeated.Key}' has {repeated.Count()} labels; a training set holds the one that stands (LabelConfirmation.Supersedes).",
                nameof(labels));

        var held = split is { } s && (s.EvaluationShare > 0 || s.BySlice?.Values.Any(v => v > 0) == true) ? s : null;
        return new TrainingSetManifest
        {
            Labels = list.Count,
            TotalWeight = list.Sum(l => l.Weight),
            BySource = Count(list, Source),
            ByTrustBasis = Count(list, l => l.TrustBasis.ToString()),
            BySlice = Count(list, l => l.SliceKey ?? string.Empty),
            ByValue = Count(list, l => valueText(l.Value)),
            From = list.Count == 0 ? null : list.Min(l => l.ConfirmedAt),
            To = list.Count == 0 ? null : list.Max(l => l.ConfirmedAt),
            EvaluationShare = held?.EvaluationShare,
            EvaluationShareBySlice = held?.BySlice,
            SplitSalt = held?.Salt,
            ContentSha256 = Hash(list, valueText),
        };
    }

    /// <summary>What changed from <paramref name="earlier"/> to <paramref name="later"/>.</summary>
    public static TrainingSetChange Compare(TrainingSetManifest earlier, TrainingSetManifest later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);
        var splitChanged = earlier.EvaluationShare != later.EvaluationShare
            || earlier.SplitSalt != later.SplitSalt
            || !SameShares(earlier.EvaluationShareBySlice, later.EvaluationShareBySlice);
        return new(
            later.Labels - earlier.Labels,
            Delta(earlier.BySource, later.BySource),
            Delta(earlier.ByTrustBasis, later.ByTrustBasis),
            Delta(earlier.BySlice, later.BySlice),
            Delta(earlier.ByValue, later.ByValue),
            splitChanged);
    }

    private static string Source<TValue>(ConfirmedLabel<TValue> label)
        => new JudgeIdentity(label.ConfirmedBy, label.JudgedBy)
            + (string.IsNullOrEmpty(label.ConfirmedByVersion) ? string.Empty : "@" + label.ConfirmedByVersion);

    private static IReadOnlyDictionary<string, int> Count<TValue>(IEnumerable<ConfirmedLabel<TValue>> labels, Func<ConfirmedLabel<TValue>, string> key)
        => labels.GroupBy(key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, int> Delta(IReadOnlyDictionary<string, int> earlier, IReadOnlyDictionary<string, int> later)
        => earlier.Keys.Union(later.Keys, StringComparer.Ordinal)
            .Select(k => (Key: k, Change: later.GetValueOrDefault(k) - earlier.GetValueOrDefault(k)))
            .Where(x => x.Change != 0)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Change, StringComparer.Ordinal);

    private static bool SameShares(IReadOnlyDictionary<string, double>? a, IReadOnlyDictionary<string, double>? b)
        => (a?.Count ?? 0) == (b?.Count ?? 0)
            && (a is null || a.All(kv => b!.TryGetValue(kv.Key, out var v) && v == kv.Value));

    private static string Hash<TValue>(IReadOnlyList<ConfirmedLabel<TValue>> ordered, Func<TValue, string> valueText)
    {
        var text = new StringBuilder();
        foreach (var label in ordered)
        {
            text.Append(label.DecisionId).Append('\t')
                .Append(valueText(label.Value)).Append('\t')
                .Append(Source(label)).Append('\t')
                .Append(label.Weight.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
