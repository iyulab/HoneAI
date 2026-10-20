# HoneAI.Core

The implementations for [HoneAI](https://github.com/iyulab/HoneAI) — tiers of judges with measured
trust and provenance — behind the [HoneAI.Abstractions](https://www.nuget.org/packages/HoneAI.Abstractions)
contracts. No third-party dependencies.

## What it ships

- **`TieredRouter<TInput, TValue>`** (`HoneAI.Routing`) — the `IReasoningRouter` over any number of
  tiers: a tier below the top settles only where the policy snapshot puts it in `Active` or `Stable`
  for the input's slice, and only an answer the settlement policy lets it settle; otherwise the
  decision goes up without the lower answer. Every lower-tier settlement is drawn for a recheck at π.
- **`RecheckDraw`** — the deterministic draw: SHA-256 of the decision id and snapshot version → [0, 1) < π.
  `TieredRouter.RecheckAsync` judges a drawn decision again at a tier above, blind — told it is a
  recheck, given no hints, no lower answer and no abstain quantile — and settles nothing.
- **`PolicySnapshotJson`** — the canonical JSON of a policy snapshot (entries by tier and slice, rates
  by knob, times in UTC) and its SHA-256 content hash, for carrying a snapshot to a judging site and
  checking it is the one issued. Signing is the consumer's.
- **`Judge.From` / `Judge.Constant` / `MemoryJudge<,>`** — reference judges: a function, a constant,
  a memory of settled answers (`System1`).
- **`SettlementPolicy`** / **`SliceKey`** — categorical, tolerance and specification-limit policies;
  slice keys from a function.
- **`HttpMLoopClient`** — `IMLoopClient` over MLoop's REST API (`mloop serve`). MLoop is reached
  over the wire, never referenced as an SDK. Construct it with an `HttpClient` whose `BaseAddress`
  and bearer token you configure (e.g. via `IHttpClientFactory`).
- **`JsonlProvenanceSink`** — file-backed `IProvenanceSink` appending each assessment as one JSON
  line. Append-only, Git-friendly.
- **`InMemoryHitlGate`** — in-process `IHitlGate` backed by a `TaskCompletionSource` per open review.
- **`ModelLifecycle`** — orchestrates train → poll → review (HITL) → promote over an `IMLoopClient`
  and an `IHitlGate`, recording each stage to an optional `IProvenanceSink`.
- **`RolePersona`** — builds domain-neutral persona instructions for a trust-loop role, with the
  consumer's domain colour injected into a `{Domain}` slot.

## Example — two tiers and a person

```csharp
using HoneAI;
using HoneAI.Routing;

var memory = new MemoryJudge<Item, string>(
    new Tier { Id = "memory", Kind = JudgeKind.System1, Version = "1", CostRank = 0 }, item => item.Key);
var model  = Judge.From<Item, string>(
    new Tier { Id = "model", Kind = JudgeKind.Ml, Version = "exp-012", CostRank = 1 },
    async (item, ctx, ct) => await PredictAsync(item, ct));
var person = Judge.From<Item, string>(
    new Tier { Id = "person", Kind = JudgeKind.Human, Version = "1", CostRank = 2 },
    async (item, ctx, ct) => await ReviewAsync(item, ct));

var router = new TieredRouter<Item, string>(
    new IJudge<Item, string>[] { memory, model, person },
    SettlementPolicy.Categorical(new[] { "OK" }, mustCatch: new[] { "NG" }),
    SliceKey.From<Item>("line-v1", item => item.Line),
    decisionIdOf: item => item.Id,
    snapshot: () => snapshot);

var decision = await router.DecideAsync(item);
// decision.Prediction — the settled value and its provenance
// decision.Steps      — every tier's verdict, including what a tier in shadow would have answered
```

## License

Apache-2.0
