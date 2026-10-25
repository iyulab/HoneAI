using System;
using System.Collections.Generic;

namespace HoneAI;

/// <summary>
/// What a training run learned from: counts of the confirmed labels by who confirmed them, how far they were trusted,
/// slice and value, the time they span, the held-out split they were drawn under, and a hash of their contents — so
/// two versions of a model can be told apart by their data, and a run can be reproduced from the same labels.
/// </summary>
/// <remarks>Built from the labels the consumer kept; HoneAI keeps none.</remarks>
public sealed record TrainingSetManifest
{
    /// <summary>Labels in the set.</summary>
    public required int Labels { get; init; }

    /// <summary>The sum of their weights — the number of decisions they stand for, counted by trust.</summary>
    public required double TotalWeight { get; init; }

    /// <summary>Labels by confirming judge and version (<c>llm@p2</c>, <c>people/p-1@1</c>, <c>llm+person-a</c>).</summary>
    public required IReadOnlyDictionary<string, int> BySource { get; init; }

    /// <summary>Labels by <see cref="LabelTrustBasis"/> name.</summary>
    public required IReadOnlyDictionary<string, int> ByTrustBasis { get; init; }

    /// <summary>Labels by slice; a label without one is counted under the empty string.</summary>
    public required IReadOnlyDictionary<string, int> BySlice { get; init; }

    /// <summary>Labels by value, as invariant text.</summary>
    public required IReadOnlyDictionary<string, int> ByValue { get; init; }

    /// <summary>When the earliest label was confirmed; null for an empty set.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>When the latest label was confirmed; null for an empty set.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>The held-out share the labels were split under; null when nothing was held out.</summary>
    public double? EvaluationShare { get; init; }

    /// <summary>The held-out share of particular slices, when the split had any.</summary>
    public IReadOnlyDictionary<string, double>? EvaluationShareBySlice { get; init; }

    /// <summary>The split's salt, when there was a split — the same salt deals the same decisions.</summary>
    public string? SplitSalt { get; init; }

    /// <summary>
    /// SHA-256 of the labels' contents (decision, value, confirming judge and version, weight), in decision order —
    /// the same labels give the same hash, whatever order they were kept in.
    /// </summary>
    public required string ContentSha256 { get; init; }
}
