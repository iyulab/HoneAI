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

    /// <summary>
    /// Labels by <see cref="LabelOrigin"/> name — how many were a person's own corrections (<c>Voluntary</c>) or answers spread over a
    /// cluster (<c>Propagated</c>), which teach at a capped weight and say nothing about the decisions nobody looked at. Empty for a set recorded before origins were counted.
    /// </summary>
    public IReadOnlyDictionary<string, int> ByOrigin { get; init; } = new Dictionary<string, int>();

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
    /// What the split's keys were (<see cref="LabelSplit.KeyKind"/>), when the split dealt by a unit bigger than the
    /// decision; null when decisions were dealt individually or nothing was held out. Two sets dealt by different
    /// kinds of unit were not drawn alike.
    /// </summary>
    public string? SplitKeyKind { get; init; }

    /// <summary>
    /// The independent units in the set — distinct split keys, a decision without one counting as its own. Fewer than
    /// <see cref="Labels"/> when decisions share a key; null for a set recorded before units were counted.
    /// </summary>
    public int? Units { get; init; }

    /// <summary>Labels offered to the selection (<c>TrainingSetSelector</c>), of which <see cref="Labels"/> were kept; null when none was made.</summary>
    public int? Offered { get; init; }

    /// <summary>Labels the selection left out, by reason (<c>unit</c>, <c>slice</c>, <c>budget</c>); null when none was made.</summary>
    public IReadOnlyDictionary<string, int>? Dropped { get; init; }

    /// <summary>
    /// Whether the labels kept carry the weight of the ones left out (<see cref="ConfirmedLabel{TValue}.SelectionProbability"/>);
    /// null when no selection was made. Without it a cap rebalances the set; with it the set stays a sample of everything.
    /// </summary>
    public bool? Compensated { get; init; }

    /// <summary>
    /// Whether the selection favored some labels over others (a priority was given) — the set then leans toward what the
    /// priority ranks first; null when no selection was made. Its keep probabilities are on the labels
    /// (<see cref="ConfirmedLabel{TValue}.SelectionProbability"/>) when <see cref="Compensated"/>.
    /// </summary>
    public bool? Prioritized { get; init; }

    /// <summary>
    /// SHA-256 of the labels' contents (decision, value, confirming judge and version, weight), in decision order —
    /// the same labels give the same hash, whatever order they were kept in.
    /// </summary>
    public required string ContentSha256 { get; init; }
}
