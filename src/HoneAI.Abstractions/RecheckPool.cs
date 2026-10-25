namespace HoneAI;

/// <summary>
/// A decision drawn for recheck and set aside for a judge that comes when it comes — a person, or an expensive model
/// run in spare time — instead of being judged on the spot. It carries what the judge is shown and nothing it must not
/// see: there is no place on it for the settled answer, or anyone else's.
/// </summary>
public sealed record PoolItem
{
    /// <summary>The decision — with <see cref="TierId"/>, the key the item is pooled under once.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The tier that settled the decision (the one being measured).</summary>
    public required string TierId { get; init; }

    /// <summary>The decision's slice.</summary>
    public required string SliceKey { get; init; }

    /// <summary>When the decision was made — windows are assigned by this.</summary>
    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>The probability π the decision was drawn with.</summary>
    public required double Probability { get; init; }

    /// <summary>The knob that set π.</summary>
    public string? Knob { get; init; }

    /// <summary>Every knob's rate when the decision was drawn.</summary>
    public IReadOnlyDictionary<string, double>? KnobRates { get; init; }

    /// <summary>The policy snapshot the decision was made under.</summary>
    public string? SnapshotVersion { get; init; }

    /// <summary>The settling tier's confidence, the selection signal's risk and weight — carried to the ledger, never shown.</summary>
    public double? Confidence { get; init; }

    /// <inheritdoc cref="Confidence"/>
    public double? Risk { get; init; }

    /// <inheritdoc cref="Confidence"/>
    public double? Weight { get; init; }

    /// <summary>
    /// Where the judge finds the input — the consumer's reference (a record id, a blob path). The input only: never a
    /// tier's answer.
    /// </summary>
    public required string InputRef { get; init; }

    /// <summary>When the item stops being offered. An item not reached by then is left out of the measurement, not counted as failed.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>What a judge is shown of a pooled item: where the input is, and nothing a tier answered.</summary>
/// <param name="DecisionId">The decision.</param>
/// <param name="TierId">The tier being measured — to answer back with.</param>
/// <param name="SliceKey">The slice.</param>
/// <param name="InputRef">Where the input is.</param>
public sealed record PoolPresentation(string DecisionId, string TierId, string SliceKey, string InputRef);

/// <summary>An item offered to a judge and held for them until a time — given back to the pool if they do not answer by then.</summary>
/// <param name="DecisionId">The decision.</param>
/// <param name="TierId">The tier being measured.</param>
/// <param name="ReviewerKey">Who holds it — a pseudonymous key.</param>
/// <param name="Until">When the hold ends.</param>
public sealed record PoolLease(string DecisionId, string TierId, string ReviewerKey, DateTimeOffset Until);

/// <summary>How a judge left an item it was shown.</summary>
public enum PoolResponseKind
{
    /// <summary>It answered.</summary>
    Answered = 0,

    /// <summary>
    /// It saw the item and passed — informative, as an upper tier's failure is: hard inputs are passed more often, so the
    /// ledger counts it at both ends of an interval. An item the judge never reached is not this.
    /// </summary>
    Skipped = 1,
}

/// <summary>A judge's response to a pooled item. The first response to an item stands.</summary>
public sealed record PoolResponse
{
    /// <summary>The decision.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The tier being measured.</summary>
    public required string TierId { get; init; }

    /// <summary>The tier that judged — the rung of people, say.</summary>
    public required string CheckedBy { get; init; }

    /// <summary>Who judged within it — a pseudonymous key.</summary>
    public string? ReviewerKey { get; init; }

    /// <summary>When.</summary>
    public required DateTimeOffset RespondedAt { get; init; }

    /// <summary>Answered or passed.</summary>
    public required PoolResponseKind Kind { get; init; }

    /// <summary>For an answer: whether it agrees with the settled one (<see cref="RecheckOutcome.Agreed"/> or <see cref="RecheckOutcome.Disagreed"/>).</summary>
    public RecheckOutcome? Outcome { get; init; }

    /// <summary>For an answer: whether it is a must-catch value.</summary>
    public bool UpperMustCatch { get; init; }
}

/// <summary>A pooled item with its hold and response, as stored.</summary>
/// <param name="Item">The item.</param>
/// <param name="Lease">The latest hold on it, if any.</param>
/// <param name="Response">The response, once there is one.</param>
public sealed record PoolEntry(PoolItem Item, PoolLease? Lease, PoolResponse? Response);

/// <summary>
/// Where pooled items, holds and responses are kept — the consumer's store; HoneAI.Core has in-memory and JSONL
/// reference implementations. Adding an item and responding are idempotent: the first of each stands.
/// </summary>
public interface IRecheckPoolStore
{
    /// <summary>Pools <paramref name="item"/> unless it is pooled already; returns whether it was added.</summary>
    ValueTask<bool> AddAsync(PoolItem item, CancellationToken cancellationToken = default);

    /// <summary>Records a hold on an item, replacing any earlier one.</summary>
    ValueTask LeaseAsync(PoolLease lease, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="response"/> unless the item has one; returns whether it was recorded.</summary>
    ValueTask<bool> RespondAsync(PoolResponse response, CancellationToken cancellationToken = default);

    /// <summary>The entries whose decision time falls in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    IAsyncEnumerable<PoolEntry> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}
