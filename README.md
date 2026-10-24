# HoneAI

[![CI](https://github.com/iyulab/HoneAI/actions/workflows/ci.yml/badge.svg)](https://github.com/iyulab/HoneAI/actions/workflows/ci.yml)
[![Release](https://github.com/iyulab/HoneAI/actions/workflows/release.yml/badge.svg)](https://github.com/iyulab/HoneAI/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/HoneAI.Agents.svg?label=HoneAI.Agents)](https://www.nuget.org/packages/HoneAI.Agents)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

> Provenance-first predictions for .NET — combine verified ML models with LLM
> reasoning, and make every answer carry its source.

HoneAI is a small .NET library for coordinating **tiers of judges** — ML models,
memories and rules, LLMs and VLMs, people — so that a cheap tier answers what it has
earned the right to answer, everything else goes up, and **every answer says which
tier gave it and why**. It is aimed at classification and value-prediction workloads
such as quality inspection, log triage and predictive maintenance.

It is built on four ideas:

1. **Tiers, cheapest first, trusted per slice** — a decision climbs a ladder of
   tiers. A lower tier settles only where it has earned that in the input's slice
   (shadow → active → stable, carried in a policy snapshot), and only with an
   answer it may settle; answers that must never be missed always go to the top.
   Confidence is not a licence to settle.
2. **Blind rechecks with recorded odds** — a share of the decisions a lower tier
   settles is drawn, deterministically and with its probability π recorded
   before anyone above answers, to be judged again by a tier above that never sees
   the lower answer. That is what lets trust be measured instead of assumed.
3. **Prediction provenance** — every prediction carries a `PredictionProvenance`
   stamp: which tier answered, in which slice and state, under which policy, what
   the tiers below it did, and whether it was drawn. *A prediction without
   provenance must not compile* — `ITracedPrediction<T>` makes the stamp
   structurally required.
4. **People are a tier** — the top of the ladder is usually a person, reached
   through an async approval gate.

## Packages

| Package | What it contains | Dependencies |
|---|---|---|
| `HoneAI.Abstractions` | Contracts only: `IJudge<,>`, `Tier`, `JudgeKind`, `ISettlementPolicy<>`, `ISliceKey<>`, `PolicySnapshot`, `PredictionProvenance`, `ITracedPrediction<T>`, `IReasoningRouter<,>`, `IMLoopClient`, `IProvenanceSink`, `IReviewStore`, `IModelLifecycle`, `ILifecycleStore`, `AgentRole` | none |
| `HoneAI.Core` | Reference implementations: `TieredRouter<,>`, `RecheckDraw`, `MemoryJudge<,>`, `SettlementPolicy`, `HttpMLoopClient`, `JsonlProvenanceSink`, `InMemoryReviewStore`, `JsonlReviewStore`, `InMemoryLifecycleStore`, `JsonlLifecycleStore`, `ModelLifecycle`, `RolePersona` | none (third-party-free) |
| `HoneAI.Agents` | An MLOps agent loop that drives the [MLoop](https://github.com/iyulab/MLoop) CLI through [mloop-mcp](https://github.com/iyulab/mloop-mcp) tools, using any `Microsoft.Extensions.AI` `IChatClient` | IronHive.Agent, ModelContextProtocol |

`HoneAI.Core` reaches ML backends over transport (HTTP), never as an SDK
reference — swapping the model server does not change your dependency graph.

All three packages are published on NuGet (versions move together):

```bash
dotnet add package HoneAI.Abstractions
dotnet add package HoneAI.Core
dotnet add package HoneAI.Agents
```

> **Security note for `HoneAI.Agents` consumers**: the `IronHive.Agent` dependency
> transitively pulls `SQLitePCLRaw.lib.e_sqlite3` ≤ 2.1.11, which carries
> [CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (High, NU1903)
> with **no patched version available upstream**. Projects that audit transitive
> packages (`NuGetAuditMode=all`, the default) with warnings-as-errors will fail to
> build; mitigate with `<NuGetAuditMode>direct</NuGetAuditMode>` or a targeted
> `<NoWarn>NU1903</NoWarn>` until upstream ships a patch.

## Quick start

### A ladder of tiers (`TieredRouter`)

Wrap each backend as a judge, say which answers a tier may settle, and route:

```csharp
using HoneAI;
using HoneAI.Routing;

var model    = Judge.From<LogLine, string>(
    new Tier { Id = "edge-model", Kind = JudgeKind.Ml, Version = "m-14", CostRank = 1 },
    async (line, ctx, ct) => await ClassifyWithModelAsync(line, ct));   // answer, or Abstain("...")
var teacher  = Judge.From<LogLine, string>(
    new Tier { Id = "teacher", Kind = JudgeKind.Llm, Version = "prompt-7", CostRank = 2 },
    async (line, ctx, ct) => await ClassifyWithLlmAsync(line, ct));
var reviewer = Judge.From<LogLine, string>(
    new Tier { Id = "reviewer", Kind = JudgeKind.Human, Version = "1", CostRank = 3 },
    async (line, ctx, ct) => await AskAPersonAsync(line, ct));

var router = new TieredRouter<LogLine, string>(
    new[] { model, teacher, reviewer },
    SettlementPolicy.Categorical(settleable: new[] { "normal", "low" }, mustCatch: new[] { "high" }),
    SliceKey.From<LogLine>("template-v1", line => line.TemplateKey),
    decisionIdOf: line => line.Id,
    snapshot: () => currentPolicySnapshot);   // null until a policy is issued: every slice in shadow

ITracedPrediction<string> prediction = await router.RouteAsync(line);

Console.WriteLine(prediction.Value);
Console.WriteLine(prediction.Provenance.TierId);         // which tier settled it
Console.WriteLine(prediction.Provenance.Sample?.Drawn);  // drawn for a blind recheck?
```

A tier settles only where the snapshot says it is `Active` or `Stable` for the
input's slice; in `Shadow` its answer is recorded (`DecideAsync` returns every
tier's verdict) and the decision goes up. A judge that throws or times out has
abstained. When the snapshot's hard expiry has passed, lower tiers go up; if
nothing above answers, the answer is used and marked `Unverified`.

### Earning trust (`HoneAI.Trust`)

A tier earns the right to settle a slice from rechecks, not from its own confidence. Each window:

1. **Ledger** — `TrustLedger.Compute` turns the window's blind rechecks into a `LedgerCell`:
   1/π-weighted agreement with its interval, the conditional must-catch miss rate with both bounds,
   the tier above's failures, the effective sample size. `TeacherLedger` measures the teacher on
   planted items whose answer is known — on a rung of people, each person on their own plants: the
   person's judge answers with `judgedBy` (a pseudonymous key, never a name), and `Compute(..., judgedBy:)`
   reads that person's cell, or the rung pooled without it.
   `DoubleCheckMeasure` reads two judges' blind answers to the same decisions: two of one kind (two
   people) by Cohen's κ on the must-catch call; two of different kinds by whether their errors coincide on
   known answers (`ErrorOverlap` — 1 is independent — with an exact p-value) and how many must-catch items
   both missed, which is what a check of one by the other cannot catch.
   `RepeatConsistency` asks one judge the same input twice, blind (the second time under another decision id,
   so it cannot remember): how often it holds its answer and its must-catch call, and on known answers how its
   errors split — `SystematicErrorRate` (the same wrong answer twice: a bias asking again will not average
   away) against `NoiseRate` (the answer moved: a disagreeing repeat is a reason to send the item up).
   `LatentTruth.Estimate` (experimental — opt in by suppressing `HONEAI001`) takes nobody's answer as the
   truth: it estimates every item's truth and every judge's confusion matrix together (Dawid–Skene, EM),
   holding planted or consensus items at their known truth. It runs only where it is identified — three or
   more judges, or some known items — and refuses otherwise.
   `LedgerShrinkage.Agreement` lets a slice with few rechecks borrow strength from the slices like it (slice →
   your group → every slice, empirical Bayes), as far as they are alike. It orders and reports — which small
   slice looks worst, what a cold one probably is — and is never what a slice gains ground on: a release rests
   on the slice's own rechecks, or a rare danger in a small slice would ride on its siblings.
2. **Lifecycle** — `TrustLifecycle.Release` moves shadow → active → stable only at a window's end,
   on every condition at once; `TrustLifecycle.Tighten` moves back at any time (a new version, a
   seen miss, agreement shown low, drift — `StalenessWatch` reads MLoop's `GET /drift`).
3. **Rate** — `RecheckRate.Solve` derives the recheck probability a stable tier needs from what you
   promise (`TrustTargets`: agreement, detectable drop, miss limit, deadline) and the slice's traffic.
4. **Pool** — `RecheckBudget.Allocate` shares the tier above's capacity, floors first, the largest
   possible misses next.
5. **Snapshot** — `PolicySnapshotIssuer.Issue` writes the `PolicySnapshot` the router judges by,
   each entry saying what set its rate.

`TrustController.Close` runs 2–5 in that order for every tier and slice. Releases wait for a person
(`LifecycleStep.AwaitsApproval`) unless `LifecyclePolicy.AutoRelease` is on; tightening never waits.
Every number a promise is made of — targets, margins, deadlines, budgets — is yours to set.

### Learning from what was confirmed (`HoneAI.Learning`)

A cheap tier gets better by learning from answers the tiers above it confirmed — never its own.
`LabelConfirmation.Decide` turns upper answers into a `ConfirmedLabel` for a learning tier:

- only a tier above the learner confirms;
- a decision that went up reached the confirming tier with certainty (π = 1); one the learner settled is
  a label only if it was drawn for recheck, at its draw's π;
- judges above that disagree where one says must-catch produce no label (`NeedsConsensus`) — neither
  side wins automatically. Two or more judges — two people on one rung count as two — then settle it together, comparing their grounds; hand the
  result (`ConsensusJudgment<T>`) to `LabelConfirmation.Settle` and it becomes the label, at the decision's π,
  trusted as `LabelTrustDefaults.Consensus` (1 unless you set it — `LabelTrustBasis.Consensus`). How the judges
  meet is yours; a consensus is also a known answer to plant for teachers (`PlantRecords.From`, truth source
  `consensus`);
- trust is the teacher ledger's measured accuracy for the confirming judge — the tier, or the person on a
  rung of people (`JudgeIdentity`) — else its kind's default (`LabelTrustDefaults`), and the label says which
  (`LabelTrustBasis`).

The label's `Weight` is trust / π. Send it to each engine's `IConfirmedLabelSink<T>`:
`MLoopFeedbackSink<T>` records it as weighted feedback on the ML tier's MLoop prediction
(`MLoopPredictionResult.PredictionId` — keep `MLoopFeedbackSink.ReferenceTo(result)` with the decision and pass it to
`Decide` as `annotations`),
which MLoop turns into a retraining set (`POST /samples`). `ModelLifecycle` closes the loop: a run started with
`LifecycleRequest.Feedback` builds that set, trains on the data with it merged in — weighted — and goes on to review
and promotion. Other engines — a memory's settlement — implement the sink themselves.

### Talking to an MLoop model server (`HttpMLoopClient`)

[`MLoop`](https://github.com/iyulab/MLoop) is an open-source AutoML CLI/server
for ML.NET. `HttpMLoopClient` implements `IMLoopClient` over its REST API
(`/predict`, `/embed`, `/train`, `/jobs/{id}`, `/promote`, `/info`, `/drift`, `/feedback`), returning
predictions already stamped with `JudgeKind.Ml` provenance — wrap it in a judge to make it a tier.

Images travel by their contents, so the MLoop server can be on another machine: `PredictImagesAsync`
classifies them with an image-classification model (one traced prediction per image, in order), and
`EmbedAsync` turns them into vectors with an encoder installed on the server — the input of a
nearest-neighbour judge over confirmed cases. The embedding names its encoder; keep it beside the vectors
and do not mix vectors of two encoders.

```csharp
var embedding = await mloop.EmbedAsync(new MLoopEmbeddingRequest([imageBytes], Encoder: "dinov2-small"));
var key = (embedding.Encoder.Id, embedding.Encoder.Sha256);   // the space these vectors live in
```

### Audit trail (`IProvenanceSink`)

`JsonlProvenanceSink` appends every provenance stamp to a JSONL file — an
append-only, grep-friendly record of what answered and why.

Records written by older versions keep reading as the current contract, and a line cut
short by a crash mid-append is skipped. The same read rules apply wherever the JSONL
ended up — a blob, a database column, an HTTP response — through
`JsonlProvenanceSink.ReadFromAsync(TextReader | Stream)`:

```csharp
await foreach (var record in JsonlProvenanceSink.ReadFromAsync(new StringReader(downloadedJsonl)))
    Console.WriteLine($"{record.SubjectId}: {record.Provenance.Kind}");
```

### Human review (`IReviewStore`)

A review is stored state, not a wait in memory: a producer opens it, a reviewer (UI,
chat-ops, CLI) decides it — in another process, hours or days later — and anyone reads
it. Open and decide are idempotent by review id; the first decision stands.
`InMemoryReviewStore` and `JsonlReviewStore` are reference implementations; operational
storage (a database table) implements the interface. `WaitForDecisionAsync` is there for a
caller that holds a process for the review anyway.

### Model lifecycle (`IModelLifecycle`)

`ModelLifecycle` orchestrates a retraining pass — train → poll job → human review →
promote — as a **resumable run**. `StartAsync` drives it to the open review and returns
(`waiting_review`); once the review is decided, `ResumeAsync(runId)` promotes (or stops on a
rejection). Every step is saved to an `ILifecycleStore`, so a run started by a queue worker is
finished by whichever process resumes it — after a restart too — and a run interrupted while
training resumes by polling its recorded job instead of training again.

```csharp
var reviews = new JsonlReviewStore("reviews.jsonl");
var lifecycle = new ModelLifecycle(mloop, reviews, new JsonlLifecycleStore("runs.jsonl"));
var run = await lifecycle.StartAsync(new LifecycleRequest(training));       // → waiting_review
// … later, wherever the reviewer is:
await reviews.DecideAsync(run.ReviewId!, new ReviewDecision("approve", ReviewerKey: "r-7"));
await lifecycle.ResumeAsync(run.RunId);                                       // → promoted, completed
```

### Agent roles

`AgentRole` names the seats an agent can occupy when reasoning about a
prediction — `Translator`, `Orchestrator`, `DomainExpert`, `Inspector`,
`Operator`, `Arbiter` — and is stamped onto provenance alongside the judge kind,
so an audit trail records not just *which tier* but *which role* produced an
answer. `HoneAI.Agents` hosts an agent loop over MLoop's MCP tools; see
[`src/HoneAI.Agents/README.md`](src/HoneAI.Agents/README.md).

## Build

```bash
dotnet build HoneAI.slnx
dotnet test HoneAI.slnx
```

Targets `net10.0`, uses Central Package Management, and builds with
warnings-as-errors.

## Status

Early development (0.x). APIs may still change between minor versions, with one
carve-out: the **contract floor** — `ITracedPrediction<T>`, `PredictionProvenance`,
`IReviewStore`, `IJudge<,>`, `JudgeKind` — is **additive-only** within 0.x. Those types may
gain a nullable property, an enum value, or a default interface method, but not a new
`required` member, a new abstract interface member, or a renumbered enum value.
`ContractFloorCompatibilityTests` enforces this; a deliberate exception ships with a
CHANGELOG migration note. See the
[`HoneAI.Abstractions` README](src/HoneAI.Abstractions/README.md#compatibility) for the
consumer-side view.

## License

[Apache-2.0](LICENSE)
