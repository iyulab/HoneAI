# HoneAI Charter

HoneAI coordinates **tiers of judges** — ML models, memory and lightweight judges ("system 1"),
LLMs and VLMs, and people — so that cheap tiers take over work as they earn trust, every answer
carries its provenance, and that trust is measured continuously rather than assumed. The goal is
low cost and high quality at the same time, a workable cold start for ML, and a system that stays
honest when the world (or one of its judges) changes.

---

## 1. Charter

| Item | Content |
|---|---|
| **Responsibility** | Between tiers: which tier answers, how much of each tier's work is re-checked, when a tier earns or loses the right to confirm answers on its own, where confirmed answers go to be learned from, and the provenance stamp on every answer. |
| **Essence** | **Coordination of judges and provenance** — whether the answer is a value or a category. The answer itself is produced by the engine inside a tier. |
| **Premise** | Nobody is ground truth, people included. Every tier, the top one too, is measured. |
| **Not** | General-purpose AI, an agent runtime, an LLM provider abstraction, a model trainer, or a structure/ontology deriver. |

## 2. Boundary

| HoneAI **does** | HoneAI does **not** |
|---|---|
| route an input up N tiers, confirming only where a tier has earned the right for that slice | train models, hold memories, or serve LLMs — those are the engines inside each tier |
| sample confirmed decisions for blind re-checking, with the inclusion probability recorded | decide what a slice key, a confirmable value, or a must-catch value means — the consumer does |
| keep a trust ledger per tier × slice and adjust sampling rates and tier eligibility from it | store the ledger in a particular database — storage is a port the consumer implements |
| route confirmed answers, with source, weight and sampling probability, to the engines that learn | transport or sign policy snapshots between sites — the consumer does |
| stamp provenance on every answer | re-abstract LLM providers (`IChatClient` is used as is) |

Engines never reference HoneAI. A tier is formed by wrapping an engine in the judge contract, in
an adapter on the consumer's side or HoneAI's — never by the engine depending on HoneAI.

### Out of scope

- RAG and document pipelines · PII masking and LLM security · provider wiring and agent loops ·
  prompt management · structure and ontology derivation.

## 3. Design constraints

1. **Provenance is structural** — a prediction without provenance does not compile (`ITracedPrediction<T>`).
2. **Measurement stays unbiased** — every re-check records the probability it was drawn with, that
   probability is never zero, and it is fixed before the upper tier answers. Selection may be as
   smart as the evidence allows; it is never deterministic exclusion.
3. **Tightening is automatic, loosening is earned** — a tier loses eligibility as soon as the ledger
   shows it should; it gains eligibility only at window boundaries under stated conditions. Targets
   and limits are set by people.
4. **Core stays dependency-free** — `HoneAI.Core` takes no third-party package; estimators are
   implemented in place.
5. **One-way dependency** — HoneAI → engines over transport or adapters; never the reverse.
   Enforced by `ImportBoundaryTests`.
6. **Provider neutrality is inherited, not re-abstracted.**

## 4. Direction (nearer = more concrete)

- **H1** — judge, tier and kind contracts, provenance v2, N-tier router, deterministic re-check
  sampler that records probabilities, policy snapshot reader. Replaces the two-tier
  `DualCheckRouter` and the fixed four-value `ReasoningLayer` (a documented breaking change).
- **H2** — re-check queue, trust ledger (inverse-probability agreement, conditional must-catch
  miss bounds, recent-window promise), selection by meaning × information, teacher qualification.
- **H3** — rate controller and tier × slice lifecycle (shadow → active → stable, demotion),
  staleness signals, policy snapshots.
- **H4** — confirmed-label routing to engines with source, weight and probability.
- **H5** — per-judge-kind trust models, double checks, repeat consistency, gold seeding for people,
  latent-truth estimation (experimental).

Each stage is accepted by measurement: agreement with the qualified teacher ≥ target, must-catch
misses ≤ limit, teacher recall on must-catch values ≥ criterion — while upper-tier calls decrease.
Hypotheses are registered before they are measured, and unfavourable results are recorded as well.
