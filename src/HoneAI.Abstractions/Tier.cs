namespace HoneAI;

/// <summary>
/// One rung of the ladder: a judge of some <see cref="Kind"/>, at a <see cref="Site"/>, in a
/// <see cref="Version"/>, with a place in the cost order. Two judges of the same kind are
/// different tiers when they sit at different sites or run different versions — an edge model and
/// a central model are measured separately.
/// </summary>
public sealed record Tier
{
    /// <summary>Stable identifier of the tier. Trust is kept per tier id and slice.</summary>
    public required string Id { get; init; }

    /// <summary>What kind of judge this is.</summary>
    public required JudgeKind Kind { get; init; }

    /// <summary>
    /// Version of the judge — model, prompt, taxonomy, rule set. When it changes, the trust this
    /// tier had earned no longer describes it.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>Position in the cost order; lower runs first.</summary>
    public required int CostRank { get; init; }

    /// <summary>Where the judge runs (an edge node, a central service), when that matters.</summary>
    public string? Site { get; init; }

    /// <summary>
    /// Whether this tier accepts hints passed up the escalation path. A hint never travels on a
    /// recheck: a recheck is blind by definition.
    /// </summary>
    public bool AcceptsHints { get; init; }
}
