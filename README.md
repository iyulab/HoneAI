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
| `HoneAI.Abstractions` | Contracts only: `IJudge<,>`, `Tier`, `JudgeKind`, `ISettlementPolicy<>`, `ISliceKey<>`, `PolicySnapshot`, `PredictionProvenance`, `ITracedPrediction<T>`, `IReasoningRouter<,>`, `IMLoopClient`, `IProvenanceSink`, `IHitlGate`, `IModelLifecycle`, `AgentRole` | none |
| `HoneAI.Core` | Reference implementations: `TieredRouter<,>`, `RecheckDraw`, `MemoryJudge<,>`, `SettlementPolicy`, `HttpMLoopClient`, `JsonlProvenanceSink`, `InMemoryHitlGate`, `ModelLifecycle`, `RolePersona` | none (third-party-free) |
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

### Talking to an MLoop model server (`HttpMLoopClient`)

[`MLoop`](https://github.com/iyulab/MLoop) is an open-source AutoML CLI/server
for ML.NET. `HttpMLoopClient` implements `IMLoopClient` over its REST API
(`/predict`, `/train`, `/jobs/{id}`, `/promote`, `/info`), returning
predictions already stamped with `JudgeKind.Ml` provenance — wrap it in a judge to make it a tier.

### Audit trail (`IProvenanceSink`)

`JsonlProvenanceSink` appends every provenance stamp to a JSONL file — an
append-only, grep-friendly record of what answered and why.

### Human review gate (`IHitlGate`)

`InMemoryHitlGate` provides an async submit → await → release flow: a pipeline
submits a flagged prediction and awaits; a reviewer (UI, chat-ops, CLI)
releases it with an approve/reject decision.

### Model lifecycle (`IModelLifecycle`)

`ModelLifecycle` orchestrates a full retraining pass — train → poll job →
human review → promote — with step tracking and optional provenance recording,
composed from the primitives above.

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
`IHitlGate`, `IJudge<,>`, `JudgeKind` — is **additive-only** within 0.x. Those types may
gain a nullable property, an enum value, or a default interface method, but not a new
`required` member, a new abstract interface member, or a renumbered enum value.
`ContractFloorCompatibilityTests` enforces this; a deliberate exception ships with a
CHANGELOG migration note. See the
[`HoneAI.Abstractions` README](src/HoneAI.Abstractions/README.md#compatibility) for the
consumer-side view.

## License

[Apache-2.0](LICENSE)
