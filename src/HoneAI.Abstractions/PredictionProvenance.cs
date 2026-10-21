using System.Collections.Generic;

namespace HoneAI;

/// <summary>
/// Traceability stamp carried by every <see cref="ITracedPrediction{T}"/> — which tier answered,
/// in which slice and state, under which policy, what the tiers below it did, and whether the
/// decision was drawn for a recheck. A value without it does not leave HoneAI.
/// </summary>
/// <remarks>
/// Generic primitive only. Domain-specific provenance (a risk level, a category) stays in
/// <see cref="Annotations"/> or the consumer adapter.
/// </remarks>
public sealed record PredictionProvenance
{
    /// <summary>The kind of judge that produced the answer.</summary>
    public required JudgeKind Kind { get; init; }

    /// <summary>The tier that produced the answer (<see cref="Tier.Id"/>).</summary>
    public string? TierId { get; init; }

    /// <summary>That tier's version at the time.</summary>
    public string? TierVersion { get; init; }

    /// <summary>
    /// Which trust-loop role produced the answer, when an agent played one. <see langword="null"/>
    /// when no role was bound.
    /// </summary>
    public AgentRole? Role { get; init; }

    /// <summary>
    /// Confidence reported by the judge, in [0.0, 1.0]; <see langword="null"/> when it reported
    /// none (a person, a memory).
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>Human-/audit-readable basis for the answer.</summary>
    public string? Rationale { get; init; }

    /// <summary>The slice the input fell in.</summary>
    public string? SliceKey { get; init; }

    /// <summary>The answering tier's state in that slice when it answered.</summary>
    public TrustState? TrustState { get; init; }

    /// <summary>Version of the policy snapshot the decision was made under.</summary>
    public string? SnapshotVersion { get; init; }

    /// <summary>The tiers the decision passed through, lowest first, and what each did.</summary>
    public IReadOnlyList<TierStep>? Path { get; init; }

    /// <summary>
    /// The recheck draw for a settled decision: its probability, the knob that set it, and whether
    /// it was drawn. Recorded before any tier above answers, so the decision can be weighted 1/π.
    /// </summary>
    public SampleDraw? Sample { get; init; }

    /// <summary>True when a hint travelled up the escalation path to the answering tier.</summary>
    public bool HadHint { get; init; }

    /// <summary>
    /// True when the decision was settled at a site whose policy had expired and no tier above
    /// could be reached — answered, but outside what the policy vouches for.
    /// </summary>
    public bool Unverified { get; init; }

    /// <summary>
    /// Domain-specific provenance fields (extension point). The middleware never interprets these;
    /// the consumer adapter writes and reads them.
    /// </summary>
    /// <remarks>
    /// Values are opaque scalar strings, and stay that way: a flat string map is what keeps a
    /// persisted provenance line greppable and diffable, and it keeps this contract free of any
    /// serializer type. A structured value is encoded by the consumer that writes it, in a form
    /// safe for arbitrary content — JSON being the natural choice. Do not join a list with a
    /// delimiter: values are consumer-supplied and may contain any delimiter you pick.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

/// <summary>What a tier did with a decision on its way up.</summary>
public enum StepOutcome
{
    /// <summary>The tier abstained, threw, or timed out.</summary>
    Abstained = 0,

    /// <summary>The tier answered, but may not settle that answer (not settleable, or one that must be caught).</summary>
    Escalated = 1,

    /// <summary>The tier answered in shadow: recorded, not settled.</summary>
    Shadowed = 2,

    /// <summary>The tier settled the decision.</summary>
    Settled = 3,
}

/// <summary>One tier's part in a decision.</summary>
/// <param name="TierId">The tier.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Outcome">What it did.</param>
/// <param name="Detail">Why — the abstain reason, or what kept it from settling.</param>
public sealed record TierStep(string TierId, JudgeKind Kind, StepOutcome Outcome, string? Detail = null);

/// <summary>The recheck draw for a settled decision.</summary>
/// <param name="Probability">π — the probability the decision was drawn with.</param>
/// <param name="Knob">Which knob set π.</param>
/// <param name="Drawn">Whether it was drawn.</param>
public sealed record SampleDraw(double Probability, string Knob, bool Drawn)
{
    /// <summary>
    /// Every knob's rate for this decision, after any selection tilt (π is the largest, floored). With
    /// the decision id and snapshot version this tells which knobs alone would have drawn it — a
    /// knob's own sub-sample, weighted by its own rate, estimates without the other knobs' tilt.
    /// </summary>
    public IReadOnlyDictionary<string, double>? KnobRates { get; init; }

    /// <summary>
    /// The selection signal the rates were tilted by, when there was one — recorded for every
    /// settled decision, drawn or not, so the calibration and the mean score can be fitted from
    /// what was decided rather than only from what was rechecked.
    /// </summary>
    public SelectionSignal? Signal { get; init; }
}
