namespace HoneAI;

/// <summary>
/// Routes a query up a ladder of tiers, cheapest first. A tier settles the decision only where it
/// has earned the right to in that slice (its <see cref="TrustState"/>) and only with an answer it
/// may settle; otherwise the decision goes up, without the lower answer.
/// </summary>
/// <typeparam name="TQuery">The input to reason about.</typeparam>
/// <typeparam name="TResult">The domain result payload.</typeparam>
public interface IReasoningRouter<in TQuery, TResult>
{
    /// <summary>
    /// Produce a result, climbing tiers until one settles it or the ladder is exhausted. The
    /// returned <see cref="PredictionProvenance"/> records which tier answered and what each tier
    /// below it did.
    /// </summary>
    Task<ITracedPrediction<TResult>> RouteAsync(TQuery query, CancellationToken cancellationToken = default);
}
