namespace HoneAI;

/// <summary>
/// Traceability stamp carried by every <see cref="ITracedPrediction{T}"/> — which
/// layer answered, how confident, on what grounds, and whether a human must review.
/// This is the "출처 있는 예측" core of HoneAI.
/// </summary>
/// <remarks>
/// Generic primitive only. Domain-specific provenance — U-Vision's
/// <c>posture</c>/<c>ml_label</c>, SMI.AIMS's <c>RiskLevel</c>/<c>Category</c>
/// (back-derivation §3.5 ②) — stays out of the middleware and lives in
/// <see cref="Annotations"/> or the consumer adapter (§2 "Adapter가 경계").
/// </remarks>
public sealed record PredictionProvenance
{
    /// <summary>Which reasoning layer produced the answer.</summary>
    public required ReasoningLayer SourceLayer { get; init; }

    /// <summary>Confidence reported by the source layer, in [0.0, 1.0].</summary>
    public required double Confidence { get; init; }

    /// <summary>Human-/audit-readable basis for the answer (근거).</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// When more than one layer answered (dual-check), whether they agreed;
    /// <see langword="null"/> when a single layer ran. Grounds
    /// <see cref="RequiresReview"/> — mirrors U-Vision's <c>agreement</c>.
    /// </summary>
    public bool? Agreement { get; init; }

    /// <summary>
    /// True when this prediction should be escalated to a human — low confidence
    /// or layer disagreement. Mirrors the consumers' <c>requires_review</c>.
    /// </summary>
    public bool RequiresReview { get; init; }

    /// <summary>
    /// Domain-specific provenance fields (extension point). The middleware never
    /// interprets these; the consumer adapter writes and reads them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}
