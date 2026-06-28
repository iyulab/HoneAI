namespace HoneAI;

/// <summary>
/// One persisted assessment: the <see cref="PredictionProvenance"/> of a prediction
/// about <see cref="SubjectId"/>, stamped with the time it was recorded.
/// </summary>
/// <param name="SubjectId">What the assessment is about — a run id, item id, or prediction id.</param>
/// <param name="Provenance">The traceability stamp being persisted.</param>
/// <param name="RecordedAt">When the sink wrote this record (write-time = the audit source of truth).</param>
public sealed record ProvenanceRecord(
    string SubjectId,
    PredictionProvenance Provenance,
    DateTimeOffset RecordedAt);

/// <summary>
/// Append-only sink for prediction provenance — the audit trail behind "출처 있는 예측".
/// Generalizes U-Vision's <c>FileMetricsStore</c> (append-only JSONL) and SMI.AIMS's
/// <c>OracleAssessment</c> + <c>PipelineStep</c> store (back-derivation §3.5 ②). Records
/// are never mutated or deleted; the storage backend is the consumer's choice.
/// </summary>
public interface IProvenanceSink
{
    /// <summary>
    /// Append an assessment for <paramref name="subjectId"/>. The sink stamps the record
    /// with its own write time and returns the persisted <see cref="ProvenanceRecord"/>.
    /// </summary>
    Task<ProvenanceRecord> AppendAsync(
        string subjectId, PredictionProvenance provenance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read recorded assessments in append order, optionally filtered to one
    /// <paramref name="subjectId"/> (null = all subjects).
    /// </summary>
    IAsyncEnumerable<ProvenanceRecord> ReadAsync(
        string? subjectId = null, CancellationToken cancellationToken = default);
}
