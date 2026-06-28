namespace HoneAI;

/// <summary>
/// Routes a query through reasoning layers cheap→costly, escalating only when a
/// layer's confidence is insufficient (§0 L0~L3 라우팅). Generalizes the escalation
/// ladders the consumers wrote independently — U-Vision's <c>AuthorityLadder</c> +
/// <c>DualCheckEvaluator</c> and SMI.AIMS's <c>LogAnalysisService</c> (ML→LLM oracle),
/// back-derivation §3.5 ①.
/// </summary>
/// <remarks>
/// Phase 0 declares the contract only. Phase 1 implements the L2(MLoop ML)↔L3
/// (IChatClient LLM) dual-check skeleton; Phase 2 extends the ladder down to
/// L1(statistics) and L0(theory).
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
