using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// One decision a teacher judged twice, blind both times — once with the input as it came and once with one piece of
/// its metadata (a source, a machine, a kind) masked by a neutral placeholder. The metadata is the consumer's: HoneAI
/// does not know which part of an input is metadata, so the masking is the caller's to do. The values are text, as the
/// teacher gave them. Null where the teacher gave no answer.
/// </summary>
public sealed record MaskedPairRecord
{
    public required string DecisionId { get; init; }

    /// <summary>The teacher.</summary>
    public required string TierId { get; init; }

    public required string SliceKey { get; init; }

    /// <summary>Which metadata was masked (<c>source</c>, <c>machine</c>).</summary>
    public required string MetaKey { get; init; }

    /// <summary>The value the input's metadata had.</summary>
    public required string MetaValue { get; init; }

    /// <summary>When the later of the two judgments was made.</summary>
    public required DateTimeOffset JudgedAt { get; init; }

    /// <summary>The teacher's answer with the input as it came.</summary>
    public string? Original { get; init; }

    /// <summary>The teacher's answer with the metadata masked.</summary>
    public string? Masked { get; init; }
}

/// <summary>
/// For one metadata value and one answer: how often masking the metadata moved the teacher off the answer, and how often
/// onto it. Without a bias the two judgments are exchangeable, so the two counts are symmetric — metadata that rightly
/// predicts the truth is carried by both and cancels.
/// </summary>
/// <param name="MetaKey">Which metadata.</param>
/// <param name="MetaValue">Its value.</param>
/// <param name="Value">The answer.</param>
/// <param name="Pairs">Pairs of this metadata value where the teacher answered both times.</param>
/// <param name="LeftValue">a — the original said the answer and the masked one did not: the metadata held the teacher on it.</param>
/// <param name="JoinedValue">c — the masked judgment said the answer and the original did not.</param>
/// <param name="P">Exact two-sided p-value of a and c being this lopsided if masking changes nothing; null with neither.</param>
/// <param name="AdjustedP">The p-value times the number of cells tested (Bonferroni), at most 1; null with no p.</param>
/// <param name="Flagged">The adjusted p-value is under the level asked for: here the teacher leans on the metadata.</param>
public sealed record MetaMaskCell(
    string MetaKey, string MetaValue, string Value, int Pairs, int LeftValue, int JoinedValue, double? P, double? AdjustedP, bool Flagged);

/// <summary>What masking an input's metadata did to a teacher's answers, over a set of pairs.</summary>
/// <param name="TierId">The teacher.</param>
/// <param name="Cells">One per metadata value and answer that took part in a pair, in key, value, answer order.</param>
/// <param name="Tests">The number of cells the correction is for.</param>
/// <param name="Level">The level a cell is flagged at.</param>
public sealed record MetaMaskReport(string TierId, IReadOnlyList<MetaMaskCell> Cells, int Tests, double Level)
{
    /// <summary>The cells where the teacher was found to lean on the metadata.</summary>
    public IEnumerable<MetaMaskCell> Flagged => Cells.Where(c => c.Flagged);
}

/// <summary>
/// Measures a teacher's bias toward an input's metadata: judge the input again with the metadata masked and see whether
/// the answers move one way for some metadata value. «Agreement with the teacher» cannot see this — a teacher that is
/// wrong in a systematic way is agreed with by a lower tier with the same bias. The measure says <em>where</em> the
/// teacher leans on the metadata, not that it is wrong: metadata can rightly bear on the answer, so a flagged cell is a
/// place to look, never a verdict, and nothing here changes a teacher's qualification.
/// </summary>
public static class MetaMaskMeasure
{
    /// <summary>
    /// The pair from a judgment of the input as it came and one with <paramref name="metaKey"/> masked.
    /// </summary>
    /// <exception cref="ArgumentException">The two are of different decisions or teachers, or one was shown another answer.</exception>
    public static MaskedPairRecord Pair<TValue>(
        RecheckResult<TValue> original, RecheckResult<TValue> masked, string sliceKey, string metaKey, string metaValue,
        Func<TValue, string>? valueText = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(masked);
        ArgumentNullException.ThrowIfNull(sliceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(metaKey);
        ArgumentNullException.ThrowIfNull(metaValue);
        if (original.DecisionId != masked.DecisionId || original.Tier.Id != masked.Tier.Id)
            throw new ArgumentException("A pair is one decision judged twice by one teacher.", nameof(masked));
        if (original.Shown || masked.Shown)
            throw new ArgumentException("Both judgments are blind: the teacher sees no other answer.", original.Shown ? nameof(original) : nameof(masked));

        valueText ??= v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        string? Text(RecheckResult<TValue> r) => r.Verdict.Answered ? valueText(r.Verdict.Value!) : null;
        return new MaskedPairRecord
        {
            DecisionId = original.DecisionId,
            TierId = original.Tier.Id,
            SliceKey = sliceKey,
            MetaKey = metaKey,
            MetaValue = metaValue,
            JudgedAt = original.JudgedAt > masked.JudgedAt ? original.JudgedAt : masked.JudgedAt,
            Original = Text(original),
            Masked = Text(masked),
        };
    }

    /// <summary>The report for <paramref name="tierId"/> over <paramref name="pairs"/>, flagging cells under <paramref name="level"/> after the correction.</summary>
    public static MetaMaskReport Compute(IEnumerable<MaskedPairRecord> pairs, string tierId, double level = 0.05)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentException.ThrowIfNullOrWhiteSpace(tierId);
        if (!(level > 0 && level < 1))
            throw new ArgumentOutOfRangeException(nameof(level), level, "A level lies strictly between 0 and 1.");

        var answered = pairs.Where(p => p.TierId == tierId && p.Original is not null && p.Masked is not null).ToList();
        var groups = answered.GroupBy(p => (p.MetaKey, p.MetaValue)).ToList();

        var raw = new List<(string Key, string Meta, string Value, int Pairs, int Left, int Joined, double? P)>();
        foreach (var group in groups)
        {
            var values = group.SelectMany(p => new[] { p.Original!, p.Masked! }).Distinct(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var left = group.Count(p => p.Original == value && p.Masked != value);
                var joined = group.Count(p => p.Original != value && p.Masked == value);
                raw.Add((group.Key.MetaKey, group.Key.MetaValue, value, group.Count(), left, joined, AnchoringMeasure.McNemarExactP(left, joined)));
            }
        }

        var tests = raw.Count;
        var cells = raw
            .OrderBy(c => c.Key, StringComparer.Ordinal).ThenBy(c => c.Meta, StringComparer.Ordinal).ThenBy(c => c.Value, StringComparer.Ordinal)
            .Select(c =>
            {
                double? adjusted = c.P is { } p ? Math.Min(1, p * tests) : null;
                return new MetaMaskCell(c.Key, c.Meta, c.Value, c.Pairs, c.Left, c.Joined, c.P, adjusted, adjusted is { } a && a < level);
            })
            .ToList();
        return new MetaMaskReport(tierId, cells, tests, level);
    }
}
