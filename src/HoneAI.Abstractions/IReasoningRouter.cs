namespace HoneAI;

/// <summary>
/// Routes a query through reasoning layers cheap→costly (L0 theory · L1 statistics ·
/// L2 ML · L3 LLM), escalating only when a layer's confidence is insufficient.
/// </summary>
/// <remarks>
/// The current implementation covers the L2 (MLoop ML) ↔ L3 (IChatClient LLM)
/// dual-check; the ladder is designed to extend down to L1 (statistics) and L0 (theory).
/// </remarks>
/// <typeparam name="TQuery">The input to reason about.</typeparam>
/// <typeparam name="TResult">The domain result payload.</typeparam>
public interface IReasoningRouter<in TQuery, TResult>
{
    /// <summary>
    /// Produce a result, climbing reasoning layers until confidence is met or the
    /// ladder is exhausted. The returned <see cref="PredictionProvenance"/> records
    /// which layer answered and whether the answer needs human review.
    /// </summary>
    Task<ITracedPrediction<TResult>> RouteAsync(TQuery query, CancellationToken cancellationToken = default);
}
