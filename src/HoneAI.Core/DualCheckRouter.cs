using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// A two-layer <see cref="IReasoningRouter{TQuery,TResult}"/> skeleton: try the cheaper
/// layer first, escalate to the costlier one only when confidence is insufficient, and
/// flag disagreement or residual low confidence for human review. Generalizes U-Vision's
/// <c>DualCheckEvaluator</c> (<c>RequiresReview = !agreement || lowConfidence</c>) and
/// SMI.AIMS's <c>LogAnalysisService</c> (confident ML skips the LLM oracle) —
/// back-derivation §3.5 ①.
/// </summary>
/// <remarks>
/// The two layers are supplied as delegates, so the router depends on neither MLoop nor an
/// LLM SDK — the consumer wires an <see cref="IMLoopClient"/> as <paramref name="lower"/>
/// and an <c>IChatClient</c>-backed call as <paramref name="escalate"/>. Domain result
/// semantics stay out of the router via <see cref="IEqualityComparer{T}"/>.
/// </remarks>
/// <typeparam name="TQuery">The input to reason about.</typeparam>
/// <typeparam name="TResult">The domain result payload.</typeparam>
public sealed class DualCheckRouter<TQuery, TResult> : IReasoningRouter<TQuery, TResult>
{
    private readonly Func<TQuery, CancellationToken, Task<ITracedPrediction<TResult>>> _lower;
    private readonly Func<TQuery, ITracedPrediction<TResult>, CancellationToken, Task<ITracedPrediction<TResult>>> _escalate;
    private readonly double _confidenceThreshold;
    private readonly IEqualityComparer<TResult> _resultComparer;

    /// <param name="lower">The cheaper layer (e.g. MLoop AutoML). Runs first.</param>
    /// <param name="escalate">The costlier layer (e.g. an LLM), given the query and the lower result.</param>
    /// <param name="confidenceThreshold">Confidence in [0,1] at/above which the lower layer is trusted without escalating.</param>
    /// <param name="resultComparer">Equality used to decide layer agreement; defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    public DualCheckRouter(
        Func<TQuery, CancellationToken, Task<ITracedPrediction<TResult>>> lower,
        Func<TQuery, ITracedPrediction<TResult>, CancellationToken, Task<ITracedPrediction<TResult>>> escalate,
        double confidenceThreshold,
        IEqualityComparer<TResult>? resultComparer = null)
    {
        _lower = lower ?? throw new ArgumentNullException(nameof(lower));
        _escalate = escalate ?? throw new ArgumentNullException(nameof(escalate));
        if (confidenceThreshold is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidenceThreshold), "Threshold must be in [0, 1].");
        _confidenceThreshold = confidenceThreshold;
        _resultComparer = resultComparer ?? EqualityComparer<TResult>.Default;
    }

    /// <inheritdoc />
    public async Task<ITracedPrediction<TResult>> RouteAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        var lower = await _lower(query, cancellationToken).ConfigureAwait(false);

        // Confident enough → trust the cheaper layer, no escalation.
        if (lower.Provenance.Confidence >= _confidenceThreshold)
            return lower;

        // Escalate and arbitrate.
        var higher = await _escalate(query, lower, cancellationToken).ConfigureAwait(false);
        var agreement = _resultComparer.Equals(lower.Value, higher.Value);
        var requiresReview = !agreement || higher.Provenance.Confidence < _confidenceThreshold;

        var provenance = new PredictionProvenance
        {
            SourceLayer = higher.Provenance.SourceLayer,
            Role = higher.Provenance.Role,   // 어느 역할이 escalate 판정했는지 보존(역할 플레이 ①)
            Confidence = higher.Provenance.Confidence,
            Agreement = agreement,
            RequiresReview = requiresReview,
            Rationale = agreement
                ? $"escalated; layers agreed (lower={lower.Provenance.SourceLayer})"
                : $"escalated; layers disagreed (lower={lower.Provenance.SourceLayer})",
            Annotations = higher.Provenance.Annotations,
        };

        return new TracedPrediction<TResult>(higher.Value, provenance);
    }
}
