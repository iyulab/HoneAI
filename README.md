# HoneAI

> **Honing AI** — "정확성은 검증 ML이, 통역은 LLM이." 출처 있는 예측·해석을 *쓸수록 정확하게*
> 만드는 제조 AI 미들웨어.

> **이름**: *hone(연마) = 쓸수록 날카로워진다* — HITL(사람 교정)→데이터 축적→MLOps 재학습→ML↑→LLM 개입↓
> 의 자가개선 순환 메타포. 표시명 **Honing AI** / repo·패키지 **HoneAI**. `hone~honest` 공명(출처 정직)은
> 덤. (구 "Reliable AI"는 Responsible/Trustworthy AI류 업계 일반어와 충돌·막연한 약속 어감으로 폐기.)

HoneAI is the **③ middle middleware** of the iyulab product stack. It is **not a new
engine** — it *assembles* MLoop (AutoML) and an LLM (via a provider-neutral `IChatClient`)
behind a small contract surface and adds three things on top:

1. **L0~L3 routing** — route a query cheap→costly: `theory(L0) → statistics(L1) →
   AutoML(L2, MLoop) → frontier(L3, LLM)`, climbing only as far as confidence requires.
2. **theory-traceability** — every prediction carries `PredictionProvenance`: which layer
   answered, how confident, on what grounds. *A prediction without provenance must not compile.*
3. **HITL** — human-approval gates for low-confidence or disagreeing answers.

## What HoneAI is — and is not

| HoneAI **is** | HoneAI is **not** |
|---|---|
| **value prediction** (demand forecast, predictive maintenance) | structure/ontology derivation (that is ② Formbase) |
| **manufacturing-flavoured** — "manufacturing AI" | "general-purpose AI" (the domain colour is named, not hidden) |
| an *assembly* of MLoop/agent (NuGet/transport **reference**) | a *re-implementation* of MLoop/agent (duplication is forbidden) |
| an agent **product** (consumer-facing trust surface) | an agent **runtime** (that is ⑤ ironhive-host) |

See [`docs/CHARTER.md`](docs/CHARTER.md) for the full charter, boundary, and phase plan.

## Status — Phase 1 (rule-of-two primitives)

Phase 0 declared the boundary in code (scaffold + zero-dependency contracts). Phase 1
extracts the five rule-of-two surfaces from the consumer back-derivation (U-Vision +
SMI.AIMS) as concrete primitives — depending on **MLoop + `IChatClient` only**. See the
[CHANGELOG](CHANGELOG.md) for the full list.

```
HoneAI.Abstractions   contracts only, zero deps     (the "출처 있는 예측" floor)
  ├─ ReasoningLayer          L0~L3 ladder
  ├─ PredictionProvenance    traceability stamp (required on every prediction)
  ├─ ITracedPrediction<T>  value + provenance
  ├─ IReasoningRouter<,>      ① cheap→costly escalation routing
  ├─ IMLoopClient            ④ transport-neutral MLoop client (HTTP ∨ MCP)
  ├─ IProvenanceSink         ② append-only assessment audit trail
  ├─ IHitlGate               ③ async human-review gate (submit→await→release)
  └─ IModelLifecycle   ⑤ train→poll→review→promote step machine
HoneAI.Core           assembly layer — Phase 1 implementations
  ├─ HttpMLoopClient         ④ over MLoop REST (predict/train/jobs/promote/info)
  ├─ JsonlProvenanceSink     ② append-only JSONL
  ├─ DualCheckRouter<,>      ① L2↔L3 confidence-gated escalation
  ├─ InMemoryHitlGate        ③ TCS review gate
  └─ ModelLifecycle    ⑤ orchestrator composing the above
```

`HoneAI.Core` carries no third-party dependencies; MLoop is reached over transport
(HTTP), never referenced as an SDK. The MCP transport for ④ is deferred to a later cycle.

## Build

```bash
dotnet build HoneAI.slnx
dotnet test HoneAI.slnx
```

net10.0 · Central Package Management · warnings-as-errors.

## Roadmap

`claudedocs/roadmap/13-honeai-middleware-roadmap.md` (in the mloop-umbrella repo) is the
canonical phase plan; the back-derivation that justifies it lives at
`claudedocs/plans/2026-06-29-honeai-consumer-backderivation.md`.
