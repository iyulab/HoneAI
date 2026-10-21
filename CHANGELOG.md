# Changelog

All notable changes to HoneAI are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and versions use semantic versioning
(0.x while the boundary stabilizes).

## [Unreleased]

### Added
- **Teacher qualification from planted items.** Agreement with a teacher is not correctness: a
  teacher that waves through what it should catch lifts a lower tier that does the same. Items whose
  answer is known (a consensus, a person's settled decision) are judged by the teacher through
  `RecheckAsync` with the plant's id, so it sees them exactly as real rechecks; `PlantRecords.From`
  records the judgment against the known answer (`PlantRecord` — `Correct`, `Wrong`, `Failed`, and
  whether the teacher answered a must-catch value). `TeacherLedger.Compute` gives a `TeacherCell`:
  must-catch recall and accuracy on the planted set, each with an exact (Clopper–Pearson) lower bound
  that counts failed judgments as misses; `TeacherLedger.Qualifies(cell, minimumRecall)` holds only
  when the recall lower bound clears the floor — five out of five does not show 0.9.
  `IPlantStore` with `InMemoryPlantStore` and `JsonlPlantStore` (once per plant and tier).
- **Fitting the selection from what was measured.** `SelectionFit.Fit(rechecks, decided, shape)`
  gives a knob's `KnobSelection`: the calibration from the answered rechecks — bins of equal
  effective size by raw risk, each bin's 1/π-weighted disagreement rate shrunk toward the overall
  rate (a small bin cannot claim zero) and made non-decreasing in risk (pool adjacent violators);
  the mean score from every settled decision's signal (one without a risk scores the mean, so the
  knob keeps its expected rate); and τ from the calibrated risk's AUC on held-out rechecks (the two
  halves of a stable split by decision id score each other): at or below chance τ = 1, else
  max(0.1, 1 − 2·(AUC − 0.5)). Short evidence (under 100 rechecks or 10 disagreements, by default)
  leaves the knob uncalibrated — uniform — and `SelectionFitResult.Reason` says why.
  `RecheckRecord.Risk` / `Weight` carry the signal from the draw; `TrustLedger.RankAuc` is the
  weighted Mann–Whitney both the ledger and the fit use.
- **Selection: a recheck probability per decision.** A recheck spent re-confirming a right answer
  buys little; `PolicyEntry.Selection` tilts a knob's rate toward the decisions worth checking
  without changing how many it draws. The consumer supplies what it knows through
  `ISelectionSignals<TInput, TValue>` — a raw `Risk` (any scale, higher = likelier wrong), a
  `Weight` (how bad a mistake would be) and a `Cluster` — and sees every tier's verdict on the way up
  (`SelectionContext`), shadow tiers included. A `KnobSelection` maps the risk through a measured
  calibration (`RiskBin`s) to p, scores it by the knob's `SelectionShape` — `Variance` √(p(1−p)) for
  agreement, `WeightedVariance` w·√(p(1−p)) for misses of what matters, `WeightedRisk` w·p for label
  harvesting — and draws at `rate × ((1 − τ) · score ÷ meanScore + τ)`, capped at 1, floored as
  before. Every decision keeps a rate above zero and the per-decision rates are recorded in
  `SampleDraw.KnobRates`, so 1/π counts stay unbiased; the tilt changes only their precision. An
  uncalibrated signal draws uniformly, a signal source that throws leaves the draw untilted, and
  `SampleDraw.Signal` keeps the signal for every settled decision so the calibration can be fitted
  from what was decided. The canonical snapshot JSON and its hash include the selection.
- **Recheck records and the trust ledger** (first part of the sampler-and-ledger stage).
  `RecheckRecord` — a settled decision that was drawn, its π and knob, its decision time, and how
  the blind recheck came out (`Agreed`, `Disagreed`, `UpperFailed`) and whether the tier above
  answered with a must-catch value. `IRecheckStore` records a decision once per tier (a redelivery
  does not count twice) and reads by decision time, so a late result lands in the window of the
  decision it belongs to; `InMemoryRecheckStore` and `JsonlRecheckStore` are reference stores.
- `TrustLedger.Compute` gives one `LedgerCell` (tier × slice × window): agreement weighted by 1/π
  (Horvitz–Thompson — unbiased however the draw was tilted), a Wilson interval on the Kish effective
  sample size with every failed recheck counted as a disagreement at the lower end and an agreement
  at the upper end, and the conditional must-catch miss rate — misses estimated at 1/π each, caught
  ones counted in full — with an exact binomial upper bound that counts failed rechecks as misses.
  With no miss seen the bound is ln(1/α)/π misses: a few hundred settled decisions at π = 0.01
  cannot show a low miss rate.
- **From the router to the ledger.** `RecheckRecords.From(decision, recheck, policy)` turns a drawn
  decision and its blind recheck into a `RecheckRecord` (agreement by the settlement policy's
  predicate, so a tolerance counts as it settles); `RecheckRecords.CaughtBy` names the tiers that
  sent a must-catch decision up. `RoutedDecision.DecidedAt` carries the decision time.
- **Per-knob counting.** `SampleDraw.KnobRates` (and `RecheckRecord.KnobRates`) keep every knob's
  rate at the draw. Because a decision is drawn when one uniform number fixed by its id and
  snapshot version falls under π, the decisions a single knob would have drawn are exactly those
  under that knob's rate — `TrustLedger.Compute(…, knob: "uniform")` counts the promise on the
  uniform knob's own sub-sample, free of the other knobs' tilt (`TrustLedger.KnobSubsample`).
- **Is confidence worth abstaining on?** `LedgerCell.ConfidenceRankAuc` — the 1/π-weighted
  probability that a disagreed recheck carried lower confidence than an agreed one (ties count
  half; `RecheckRecord.Confidence` from the decision's provenance). A tier should beat 0.5 before
  abstaining on its least confident answers means anything.
- **Recent window.** `TrustLedger.RecentWindowStart(records, tier, slice, asOf, decisions)` finds the
  window that stands for the last N settled decisions (recent rechecks until their 1/π weights add up
  to N) — a cumulative promise lags a tier that has moved.

## [0.3.0] - 2026-10-21

### Changed — breaking
- **Reasoning layers became judges and tiers.** `ReasoningLayer` is replaced by `JudgeKind`
  (`Theory`, `Statistics`, `Ml`, `Llm`, `System1`, `Vlm`, `Human`) and `Tier` — a judge of some kind
  at a site, in a version, with a place in the cost order — so two judges of one kind at different
  sites, or a memory-backed judge, can be told apart. New contracts: `IJudge<TInput, TValue>` (answers
  or abstains, given the input only), `ISettlementPolicy<TValue>` (which answers a tier may settle,
  which must always go up, when two answers agree — predicates, so measured values fit), `ISliceKey<TInput>`,
  and the `PolicySnapshot` read model (`TrustState` per tier and slice, recheck probabilities, soft
  and hard expiry). Core adds `SettlementPolicy.Categorical` / `Tolerance` / `SameSideOf` and `SliceKey`.
- **`PredictionProvenance` v2.** `SourceLayer` → `Kind` (the only `required` member);
  `Confidence` is now optional (`double?`) — a person or a memory may report none. `Agreement` and
  `RequiresReview` are replaced by `Path`, the tiers a decision passed through and what each did
  (`Abstained`, `Escalated`, `Shadowed`, `Settled`). Added: `TierId`, `TierVersion`, `SliceKey`,
  `TrustState`, `SnapshotVersion`, `Sample` (the recheck draw: π, the knob that set it, whether it was
  drawn), `HadHint`, `Unverified`.
- **`ReviewDecision` takes a `ReviewerKey`** — a pseudonymous key, so trust can be measured per
  reviewer. Never a name.
- **`DualCheckRouter` is removed**, replaced by **`TieredRouter<TInput, TValue>`** (`HoneAI.Routing`):
  any number of tiers ordered by cost; a tier below the top settles only where the policy snapshot
  puts it in `Active` or `Stable` for the input's slice and only an answer the settlement policy
  lets it settle — confidence is not a licence; must-catch answers always reach the top; a decision
  that goes up carries no lower answer, and hints travel only to a tier that accepts them; a judge
  that throws or times out has abstained; after the snapshot's hard expiry lower tiers go up and,
  if nothing above answers, the held answer is used and marked `Unverified`; a snapshot computed
  under another slice-key definition does not apply. `DecideAsync` returns every tier's verdict
  (what a shadow tier would have answered), and `DecisionNotSettledException` carries the path when
  no tier settles. Migration: wrap the two delegates as judges (`Judge.From`) and express the
  confidence threshold, if you keep one, as the lower judge abstaining below it.
- **Recheck draw** (`RecheckDraw`) — every decision a lower tier settles is drawn at π (the largest of
  the entry's knob rates, never below `TieredRouterOptions.MinimumSampleRate`, default 0.01) from
  SHA-256 of the decision id and snapshot version, and recorded in `Provenance.Sample` before any
  tier above answers — deterministic across sites and retries.
- **Reference judges**: `Judge.From`, `Judge.Constant`, `MemoryJudge<,>` (`System1`). `JudgeVerdict`
  can carry hints for a tier above.
- **Blind recheck** — `TieredRouter.RecheckAsync(input, decisionId, tierId)` judges a settled decision
  again at the named tier with `IsRecheck` set and no hints, lower answer or abstain quantile, and
  settles nothing; a recheck that throws or times out comes back as an abstained verdict ("drawn but
  not judged"), not an error.
- **`PolicySnapshotJson`** — canonical serialization of a `PolicySnapshot` (one byte sequence per
  content) and its SHA-256 `ContentHash`; `Deserialize` reads it back.
- `ModelLifecycle` records the review step as `JudgeKind.Human` (it was recorded as the LLM layer).
- **Migration**: `ReasoningLayer.AutoMl` → `JudgeKind.Ml`, `Frontier` → `Llm`, `Theory` and
  `Statistics` keep their names (numbers 0–3 unchanged). `new PredictionProvenance { SourceLayer = x,
  Confidence = c }` → `{ Kind = x, Confidence = c }`. Read `Confidence` as nullable. Where you read
  `RequiresReview`, read whether the decision was settled by a person or reached the top of the path
  unsettled; where you read `Agreement`, compare the answers you recorded.
- **Persisted provenance still reads.** `JsonlProvenanceSink` maps a 0.2.x line's `sourceLayer` to
  `kind` (`AutoMl` → `Ml`, `Frontier` → `Llm`) and keeps its `agreement` / `requiresReview` as
  `legacy:agreement` / `legacy:requiresReview` annotations — an audit trail is never rewritten.
- The contract floor is re-declared for the new shape (`HoneAI.Abstractions` README, «Compatibility»;
  `ContractFloorCompatibilityTests`): `PredictionProvenance` requires only `Kind`, `IJudge` has two
  abstract members, `JudgeKind` numbers are pinned.

### Changed
- **Charter revised** (`docs/CHARTER.md`) — HoneAI's essence is now stated as coordinating tiers
  of judges (ML, memory/lightweight judges, LLM/VLM, people) with measured trust and provenance,
  for values and categories alike, rather than value prediction alone. The direction section lists
  the stages that will replace `DualCheckRouter` and `ReasoningLayer`; no code changes in this entry.

## [0.2.2] - 2026-09-09

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
