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

## Compatibility

HoneAI is 0.x, so APIs may still change between minor versions — except the **contract
floor**, the four types you can take on their own without any other HoneAI package:

| Type | What stays fixed within 0.x |
|---|---|
| `ITracedPrediction<T>` | exactly `Value` and `Provenance` |
| `PredictionProvenance` | only `SourceLayer` and `Confidence` are `required`; every other member is optional (nullable or `bool`) |
| `IHitlGate` | `AwaitDecisionAsync` and `Submit` are the only abstract members; new capability arrives as a default interface method |
| `ReasoningLayer` | `Theory=0`, `Statistics=1`, `AutoMl=2`, `Frontier=3` keep their numbers (persisted provenance depends on them) |

These may grow, but code written against them today keeps compiling: an object initializer,
a custom `IHitlGate` implementation, or a switch over `ReasoningLayer` will not be broken by a
minor release. `ContractFloorCompatibilityTests` in the repository enforces the table above;
a deliberate exception is a documented breaking change with a CHANGELOG migration note.

Two things the floor does **not** promise: `Confidence` is expected in `[0.0, 1.0]` but not
validated by the contract (the producing layer is responsible), and the in-process
`InMemoryHitlGate` in `HoneAI.Core` is single-process by design — a review that must
survive a process or request boundary needs an `IHitlGate` over your own store.

## License

Apache-2.0
