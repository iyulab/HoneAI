# HoneAI.Abstractions

The contract surface for [HoneAI](https://github.com/iyulab/HoneAI) — the "provenance-bearing
prediction" middleware. **Zero dependencies by design**: every value that flows out of HoneAI
carries how it was made, so *a prediction without provenance must not compile*.

This package is interfaces and records only. Concrete implementations live in
[HoneAI.Core](https://www.nuget.org/packages/HoneAI.Core); reference this package when you write
your own adapters or want to depend on the contracts without the implementations.

## Core contracts

- **`ITracedPrediction<T>`** / **`PredictionProvenance`** — a predicted value that cannot exist
  without its provenance stamp: which tier answered, in which slice and trust state, under which
  policy snapshot, what the tiers below it did, and whether it was drawn for a recheck.
  Domain-specific fields live in `Annotations`, never in the middleware.
- **`IJudge<TInput, TValue>`** / **`Tier`** / **`JudgeKind`** — a judge answers or abstains, given
  the input only; a tier is a judge of some kind (theory, statistics, ML, System1, LLM, VLM, a
  person) at a site, in a version, with a place in the cost order.
- **`ISettlementPolicy<TValue>`** — which answers a tier may settle, which must always go up, and
  when two answers agree — predicates, so measured values fit as well as categories.
- **`ISliceKey<TInput>`** / **`PolicySnapshot`** / **`TrustState`** — trust is earned per tier and
  slice; a snapshot carries each slice's state and recheck probabilities to the judging site.
- **`IReasoningRouter<TQuery, TResult>`** — routes a query up the tiers, cheapest first.
- **`IMLoopClient`** — transport-neutral client for a running MLoop instance (HTTP or MCP-stdio).
- **`IReviewStore`** / **`ReviewDecision`** / **`ReviewRecord`** — human review as stored state: opened, decided once, read by id.
- **`IProvenanceSink`** / **`ProvenanceRecord`** — append-only audit trail for predictions.
- **`IModelLifecycle`** / **`ILifecycleStore`** — a resumable train→evaluate→review→promote run and where it is kept between steps.
- **`AgentRole`** / **`RoleContext`** — the trust-loop role an agent plays, stamped onto provenance.

## Design boundary

The middleware is domain-neutral. Domain meaning (verdict vocabulary, a manufacturing process
name, a risk category) is injected by the consumer adapter via `RoleContext.Domain` and
`PredictionProvenance.Annotations` — the adapter is the domain boundary, never HoneAI itself.

## Compatibility

HoneAI is 0.x, so APIs may still change between minor versions — except the **contract
floor**, the types you can take on their own without any other HoneAI package:

| Type | What stays fixed within 0.x |
|---|---|
| `ITracedPrediction<T>` | exactly `Value` and `Provenance` |
| `PredictionProvenance` | only `Kind` is `required`; every other member is optional (nullable or `bool`) |
| `IReviewStore` | `OpenAsync`, `DecideAsync`, `GetAsync` and `ListPendingAsync` are the only abstract members; new capability arrives as a default interface method |
| `IJudge<TInput, TValue>` | `Tier` and `JudgeAsync` are the only abstract members |
| `JudgeKind` | `Theory=0`, `Statistics=1`, `Ml=2`, `Llm=3`, `System1=4`, `Vlm=5`, `Human=6` keep their numbers (persisted provenance depends on them) |

These may grow, but code written against them today keeps compiling: an object initializer,
a custom `IReviewStore` or `IJudge` implementation, or a switch over `JudgeKind` will not be broken
by a minor release. `ContractFloorCompatibilityTests` in the repository enforces the table above;
a deliberate exception is a documented breaking change with a CHANGELOG migration note.

**0.3.0 was such an exception**: `ReasoningLayer` became `JudgeKind` + `Tier`, and
`PredictionProvenance.SourceLayer` / `Agreement` / `RequiresReview` became `Kind` and the
per-tier `Path` (see the CHANGELOG for the migration). Persisted records written by 0.2.x still
read: the JSONL sink maps `sourceLayer` to `kind`, `AutoMl` to `Ml` and `Frontier` to `Llm`, and
keeps the old agreement and review flags as `legacy:` annotations.

**0.9.0 was another**: `IHitlGate` — a decision awaited in process memory — became `IReviewStore`, a
stored review, so a review survives a restart and a process boundary. Open the review with
`OpenAsync` where you called `AwaitDecisionAsync`, decide with `DecideAsync` where you called
`Submit`, and continue a lifecycle run with `IModelLifecycle.ResumeAsync` (see the CHANGELOG).

Three things the floor does **not** promise: `Confidence` is expected in `[0.0, 1.0]` but not
validated by the contract (the producing layer is responsible); the reference review stores in
`HoneAI.Core` are for tests and small deployments — `InMemoryReviewStore` lives and dies with its
process, `JsonlReviewStore` reads its whole file per call — so operational reviews belong in an
`IReviewStore` over your own database; and
`PredictionProvenance.Annotations` carries **opaque scalar strings**, which the middleware
never parses. A structured annotation value is encoded by the consumer that writes it, in a
form safe for arbitrary content — JSON being the natural choice. Joining a list with a
delimiter is not: annotation values are consumer-supplied and may contain whichever
delimiter you pick. The map stays flat so a persisted provenance line stays greppable and
diffable, and so this contract carries no serializer type.

## License

Apache-2.0
