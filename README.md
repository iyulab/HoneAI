# HoneAI

[![CI](https://github.com/iyulab/HoneAI/actions/workflows/ci.yml/badge.svg)](https://github.com/iyulab/HoneAI/actions/workflows/ci.yml)
[![Release](https://github.com/iyulab/HoneAI/actions/workflows/release.yml/badge.svg)](https://github.com/iyulab/HoneAI/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/HoneAI.Agents.svg?label=HoneAI.Agents)](https://www.nuget.org/packages/HoneAI.Agents)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

> Provenance-first predictions for .NET — combine verified ML models with LLM
> reasoning, and make every answer carry its source.

HoneAI is a small .NET library for building prediction pipelines that pair
machine-learning models with LLMs **without losing track of which one answered,
how confident it was, and whether a human should review it**. It is aimed at
value-prediction workloads such as demand forecasting, predictive maintenance,
and quality inspection.

It is built on three ideas:

1. **Layered reasoning, cheap-first** — route a query up a ladder of reasoning
   layers, `Theory (L0) → Statistics (L1) → AutoML (L2) → LLM (L3)`, escalating
   to a costlier layer only when the cheaper one is not confident enough.
2. **Prediction provenance** — every prediction carries a
   `PredictionProvenance` stamp: which layer answered, with what confidence, on
   what grounds, and whether layers disagreed. *A prediction without provenance
   must not compile* — the `ITracedPrediction<T>` type makes the stamp
   structurally required.
3. **Human-in-the-loop gates** — low-confidence or disagreeing answers are
   flagged `RequiresReview` and can be parked at an async approval gate until a
   human releases them.

## Packages

| Package | What it contains | Dependencies |
|---|---|---|
| `HoneAI.Abstractions` | Contracts only: `ReasoningLayer`, `PredictionProvenance`, `ITracedPrediction<T>`, `IReasoningRouter<,>`, `IMLoopClient`, `IProvenanceSink`, `IHitlGate`, `IModelLifecycle`, `AgentRole` | none |
| `HoneAI.Core` | Reference implementations: `DualCheckRouter<,>`, `HttpMLoopClient`, `JsonlProvenanceSink`, `InMemoryHitlGate`, `ModelLifecycle`, `RolePersona` | none (third-party-free) |
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

### Confidence-gated escalation (`DualCheckRouter`)

Run the cheap layer first; escalate to the expensive one only when confidence
falls short; flag disagreement for human review:

```csharp
using HoneAI;

var router = new DualCheckRouter<SensorWindow, string>(
    lower:    (query, ct)           => PredictWithMlAsync(query, ct),        // e.g. an AutoML model
    escalate: (query, mlResult, ct) => JudgeWithLlmAsync(query, mlResult, ct), // e.g. an IChatClient call
    confidenceThreshold: 0.85);

ITracedPrediction<string> prediction = await router.RouteAsync(window);

Console.WriteLine(prediction.Value);
Console.WriteLine(prediction.Provenance.SourceLayer);   // AutoMl (no escalation) or Frontier
Console.WriteLine(prediction.Provenance.Confidence);

if (prediction.Provenance.RequiresReview)
{
    // layers disagreed, or confidence stayed low — send to a human
}
```

Both layers are plain delegates, so the router depends on neither an ML SDK nor
an LLM SDK — wire in whatever backends you use.

### Talking to an MLoop model server (`HttpMLoopClient`)

[`MLoop`](https://github.com/iyulab/MLoop) is an open-source AutoML CLI/server
for ML.NET. `HttpMLoopClient` implements `IMLoopClient` over its REST API
(`/predict`, `/train`, `/jobs/{id}`, `/promote`, `/info`), returning
predictions already stamped with `ReasoningLayer.AutoMl` provenance.

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
`Operator`, `Arbiter` — and is stamped onto provenance alongside the layer, so
an audit trail records not just *which layer* but *which role* produced an
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

Early development (0.x). The contract surface in `HoneAI.Abstractions` is
stabilizing; APIs may still change between minor versions.

## License

[Apache-2.0](LICENSE)
