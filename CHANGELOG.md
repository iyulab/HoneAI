# Changelog

All notable changes to HoneAI are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and versions use semantic versioning
(0.x while the boundary stabilizes).

## [Unreleased]

### Added
- **Contract-floor compatibility policy** — `ITracedPrediction<T>`, `PredictionProvenance`,
  `IHitlGate` and `ReasoningLayer` are now declared additive-only within 0.x (no new `required`
  member, no new abstract interface member, no renumbered enum value). Documented in the
  `HoneAI.Abstractions` README (`## Compatibility`) and enforced by
  `ContractFloorCompatibilityTests`, so a consumer that references only the contracts can rely
  on minor releases not breaking existing object initializers, gate implementations, or
  persisted provenance records.
- **`Annotations` value semantics are now stated in the contract** — `PredictionProvenance.Annotations`
  carries opaque scalar strings; a structured value is encoded by the consumer that writes it, in a
  form safe for arbitrary content, and never by joining with a delimiter (annotation values are
  consumer-supplied and may contain any delimiter). Documented on the property and in the
  `HoneAI.Abstractions` README (`## Compatibility`), and pinned by `ContractFloorCompatibilityTests`
  so the value type cannot widen without that being a deliberate, documented change.

## [0.2.1] - 2026-07-15

### Changed
- **Dependency modernization** — `IronHive.Agent` 0.2.16 → 0.3.0, `Microsoft.Extensions.AI` +
  `Microsoft.Extensions.AI.Abstractions` 10.6.0 → 10.8.0, `Microsoft.Extensions.Logging.Abstractions`
  10.0.8 → 10.0.10. IronHive.Agent's pre-1.0 minor bump is compatible with the `HoneAI.Agents`
  engine wiring — verified by clean build and all 80 tests passing, including `ImportBoundaryTests`
  (HoneAI.Core remains zero-dependency).

## [0.2.0] - 2026-07-08

First version where all three packages (`HoneAI.Abstractions`, `HoneAI.Core`,
`HoneAI.Agents`) are published to NuGet with a single shared version.

### Added
- ④ `IMLoopClient.ForecastAsync` + `HttpMLoopClient` implementation — horizon-based forecasting
  over MLoop 0.20+'s `POST /predict` forecasting contract (`{"horizon":N}` object body, `{}` =
  trained horizon). Returns `MLoopForecastResult` (ordered points with native SSA confidence
  bands) wrapped in `ITracedPrediction` with `ReasoningLayer.AutoMl` provenance; the forecast's
  confidence is its weakest step's (later steps widen their band). A mismatched horizon surfaces
  MLoop's actionable 400 as `MLoopClientException` instead of an empty forecast.
- `HoneAI.Abstractions` and `HoneAI.Core` NuGet publishing gate opened (`IsPackable=true`) —
  previously consumed from source only.

### Security
- Known transitive advisory in `HoneAI.Agents`: `IronHive.Agent` pulls
  `SQLitePCLRaw.lib.e_sqlite3` ≤ 2.1.11 with
  [CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (High, NU1903);
  no patched version exists upstream. Consumers auditing transitive packages should
  set `NuGetAuditMode=direct` or `NoWarn NU1903` (see package README).

## [0.1.0] - 2026-07-05

First NuGet release — `HoneAI.Agents` only (`MloopAgent` + `McpMloopToolProvider`,
relocated from the now-deprecated `mloop-agent` package).

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
