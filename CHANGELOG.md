# Changelog

All notable changes to HoneAI are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and versions use semantic versioning
(0.x while the boundary stabilizes).

## [Unreleased]

### Added — Phase 0 (boundary + scaffold)

- Solution scaffold: `HoneAI.Abstractions` (contracts, zero dependencies),
  `HoneAI.Core` (assembly layer), `HoneAI.Tests`; net10, Central Package
  Management, warnings-as-errors.
- Charter + boundary docs (`README.md`, `docs/CHARTER.md`).
- Contract surface (`HoneAI.Abstractions`, interfaces only):
  - `ReasoningLayer` (L0~L3 ladder), `PredictionProvenance` + `ITracedPrediction<T>`
    ("a prediction without provenance must not compile").
  - `IReasoningRouter<,>` ①, `IMLoopClient` ④, `IHitlGate` ③, `IModelLifecycle` ⑤,
    `IProvenanceSink` ② — the five rule-of-two surfaces from the consumer back-derivation.
- Import-boundary tests (declared-graph + runtime) and a CI workflow that runs them
  (build/test on ubuntu/windows/macos + vulnerable-package scan).

### Added — Phase 1 (rule-of-two primitive extraction · MLoop + IChatClient only)

- ④ `HttpMLoopClient` — transport client over MLoop's REST API
  (`/predict`, `/train`, `/jobs/{id}`, `/promote`, `/info`); predictions carry
  `ReasoningLayer.AutoMl` provenance with a clamped confidence.
- ② `JsonlProvenanceSink` — append-only JSONL audit sink for `PredictionProvenance`.
- ① `DualCheckRouter<,>` — lower-first, confidence-gated escalation with an
  agreement/residual-confidence review signal (delegate-based; no LLM SDK dependency).
- ③ `InMemoryHitlGate` — TCS-based async human-review gate (submit → await → release).
- ⑤ `ModelLifecycle` — train → poll → review (HITL) → promote orchestration with
  step tracking and optional provenance recording.

### Notes

- `HoneAI.Core` carries no third-party package dependencies; MLoop is reached over
  transport (HTTP), never referenced as an SDK.
- Deferred: MCP transport (`IMLoopClient` over mloop-mcp stdio) — a second transport for ④.
- Consumer adoption proof (R-7 — U-Vision / SMI.AIMS shedding their hand-written code) is
  out of this repo's scope; contracts are grounded against both consumers' exact shapes.
