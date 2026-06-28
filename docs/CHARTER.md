# HoneAI Charter

**Position**: iyulab product-middleware **③** (middle layer). Assembles MLoop·mloop-agent
(low-level assets) to provide a "predictions with provenance" surface.
**Canonical vision**: `dev-works/org/docs/product-middleware.md` §4 ③.
**Demand source (back-derivation)**: `mloop-umbrella/claudedocs/plans/2026-06-29-honeai-consumer-backderivation.md`.

---

## 1. Charter (5 items)

| Item | Content |
|---|---|
| **Responsibility** | "Accuracy from verified ML, interpretation from the LLM" — predictions/interpretations with provenance. L0~L3 routing + theory-traceability + HITL. |
| **Boundary** | *Value prediction* is the essence (structure derivation belongs to ② Formbase). Manufacturing-flavoured → **not** "general-purpose AI". |
| **Consumers** | U-Solutions (demand forecast / predictive maintenance) · U-Vision · Forge (analysis assist). |
| **Dependencies** (direct assembly; middle-layer, so the without-X abstraction is waived) | MLoop · mloop-agent · mloop-mcp · u-insight · u-analytics · formulab · ironhive-host (*reference*, never duplicate) · ⑥ iron-prow. |
| **Extraction condition** | **Q1** — confirmed: U-Vision + SMI.AIMS already re-implement the same 5 patterns independently (rule-of-two). |

> DataLens is ② (structure/pattern derivation = Formbase), **not** ③. ③'s statistics come from
> u-insight / u-analytics.

---

## 2. Boundary — what it is NOT (honest)

| HoneAI **is** | HoneAI is **not** |
|---|---|
| value prediction (forecast, predictive maintenance) | structure/ontology derivation (② Formbase) |
| manufacturing-flavoured ("manufacturing AI") | "general-purpose AI" |
| an *assembly* of MLoop/agent (reference) | a *re-implementation* of MLoop/agent |
| an agent *product* (consumer-facing trust surface) | an agent *runtime* (⑤ ironhive-host) |

### Out of scope (over-scope guards — back-derivation §2)

- **RAG / document pipelines** → ④ FluxIndex / FluxFeed.
- **PII masking / LLM security** → FluxGuard / FluxCurator.
- **provider wiring / agent loop** → ⑤ ironhive-host / ⑥ iron-prow (`IChatClient` is already
  provider-neutral; HoneAI must **not** re-abstract providers).
- **prompt versioning** → rule-of-one today (only SMI builds it); wait for demand.

---

## 3. Design constraints (violation = loss of middleware standing)

1. **Reference, not duplication** (product-middleware §2.1/§2.2) — the middle layer may depend
   directly on MLoop/agent/ironhive, but it *references* them, never *copies* them (avoids
   version divergence).
2. **One-way import direction** (§6.1) — HoneAI → MLoop/agent only. MLoop/agent never know
   HoneAI (reverse reference = cycle). Enforced by `HoneAI.Tests/ImportBoundaryTests`.
3. **Provider neutrality is inherited, not re-abstracted** (§2.1) — iron-prow / ironhive-host
   already expose `IChatClient`; HoneAI does not wrap providers again.
4. **Value vs structure layer line** (§4 ②③) — value prediction only. Structure/ontology
   requests route to ② Formbase (no absorption).

### How MLoop is consumed (clarifies roadmap/13 0-4)

roadmap/13 0-4 says "reference MLoop via NuGet". Two facts refine that wording:
- MLoop's **SDK packages are deliberately not published to NuGet** (only the `mloop` CLI tool is).
- The consumer evidence (back-derivation §3.5 ④) shows both consumers reach MLoop over
  **transport — HTTP (U-Vision) and MCP stdio (SMI.AIMS)** — not via a compile-time SDK reference.

So the Phase 0 boundary is really about **direction** (one-way, no reverse reference) and **no
source duplication**, and the Phase 1 `IMLoopClient` binds to a *running MLoop* over HTTP/MCP.
There is no compile-time `PackageReference` to MLoop.Core.

---

## 4. Phases (demand-driven — nearer = more concrete)

- **Phase 0 (NOW · ungated)** — boundary + scaffold + contracts (`ReasoningLayer`,
  `PredictionProvenance`, `ITracedPrediction<T>`, `IReasoningRouter<,>`, `IMLoopClient`).
  *Interfaces only.* This document + the import-boundary test pin the boundary.
- **Phase 1 (demand-confirmed · ungated by upper middleware)** — extract the 5 rule-of-two
  primitives, MLoop + `IChatClient` only: **④ MLoop client SDK → ② provenance sink → ① routing
  skeleton (L2↔L3) → ③ HITL gate → ⑤ lifecycle orchestrator**. Each extraction is validated by
  one consumer (U-Vision ∨ SMI) actually adopting it and shedding code (R-7).
- **Phase 2 (gated by upper middleware)** — extend the ladder to L1 (u-analytics) and L0
  (formulab) for a full L0~L3 router; route L3 via iron-prow.
- **Phase 3 (direction)** — manufacturing trust flywheel + U-Solutions / U-Vision consumption.

---

## 5. The 5 rule-of-two surfaces (back-derivation §3.5)

U-Vision and SMI.AIMS independently re-implement the same five patterns — code evidence, not
speculation. HoneAI absorbs the *generic primitive*; domain meaning stays in the consumer
adapter (§2 "Adapter is the boundary").

| Surface | U-Vision | SMI.AIMS | HoneAI absorbs |
|---|---|---|---|
| ① reasoning routing | `AuthorityLadder` + `DualCheckEvaluator` | `LogAnalysisService` (ML→LLM) | `IReasoningRouter` escalation ladder |
| ② prediction provenance | `FileMetricsStore` + `FileModelRegistry` | `OracleAssessment` + `PipelineStep` | `PredictionProvenance` schema + sink |
| ③ HITL gate | review queue + `requires_review` | `SubmitReview` (TCS gate) | async review-gate primitive |
| ④ MLoop client | HTTP `MloopClassifier` | MCP `MLoopMcpService` + HTTP | transport-unified `IMLoopClient` |
| ⑤ lifecycle orchestration | `FileDatasetExporter` + activate | `MLoopPipelineService` | export→train→eval→review→promote step machine |
