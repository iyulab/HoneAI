# HoneAI.Core

The assembly layer for [HoneAI](https://github.com/iyulab/HoneAI) — the "provenance-bearing
prediction" middleware. Composes an MLoop AutoML backend (L2, reached over HTTP/MCP transport) and
an `IChatClient` frontier model (L3) behind the [HoneAI.Abstractions](https://www.nuget.org/packages/HoneAI.Abstractions)
contracts.

## What it ships

- **`DualCheckRouter<TQuery, TResult>`** — the two-layer `IReasoningRouter`: run the cheaper layer
  first, escalate to the costlier one only when confidence is insufficient, and flag disagreement
  or residual low confidence for human review.
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

## Example — confidence-gated dual-check

```csharp
using HoneAI;

var mloop = new HttpMLoopClient(httpClient);            // HTTP client → `mloop serve`
var router = new DualCheckRouter<MyQuery, MyResult>(
    lower:     (q, ct) => CallMLoopAsync(mloop, q, ct), // L2 AutoML, runs first
    escalate:  (q, lower, ct) => AskLlmAsync(q, ct),    // L3 LLM, only when unsure
    confidenceThreshold: 0.8);

var traced = await router.RouteAsync(query);
if (traced.Provenance.RequiresReview)
{
    // low confidence or layer disagreement → route to a human via IHitlGate
}
```

## License

Apache-2.0
