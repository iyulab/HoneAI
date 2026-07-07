# HoneAI.Abstractions

The contract surface for [HoneAI](https://github.com/iyulab/HoneAI) — the "provenance-bearing
prediction" middleware. **Zero dependencies by design**: every value that flows out of HoneAI
carries how it was made, so *a prediction without provenance must not compile*.

This package is interfaces and records only. Concrete implementations live in
[HoneAI.Core](https://www.nuget.org/packages/HoneAI.Core); reference this package when you write
your own adapters or want to depend on the contracts without the implementations.

## Core contracts

- **`ITracedPrediction<T>`** / **`PredictionProvenance`** — a predicted value that cannot exist
  without its provenance stamp (which reasoning layer answered, confidence, rationale, whether a
  human must review). Domain-specific fields live in `Annotations`, never in the middleware.
- **`IReasoningRouter<TQuery, TResult>`** — routes a query through reasoning layers cheap→costly
  (L0 theory · L1 statistics · L2 ML · L3 LLM), escalating only when confidence is insufficient.
- **`IMLoopClient`** — transport-neutral client for a running MLoop instance (HTTP or MCP-stdio).
- **`IHitlGate`** / **`ReviewDecision`** — async human-in-the-loop review gate keyed by review id.
- **`IProvenanceSink`** / **`ProvenanceRecord`** — append-only audit trail for predictions.
- **`IModelLifecycle`** — orchestrates export→init→inspect→train→evaluate→review→promote.
- **`ReasoningLayer`** / **`AgentRole`** / **`RoleContext`** — the layer/role taxonomy stamped onto
  every provenance record.

## Design boundary

The middleware is domain-neutral. Domain meaning (verdict vocabulary, a manufacturing process
name, a risk category) is injected by the consumer adapter via `RoleContext.Domain` and
`PredictionProvenance.Annotations` — the adapter is the domain boundary, never HoneAI itself.

## License

Apache-2.0
