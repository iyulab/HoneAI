# Changelog

All notable changes to HoneAI are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and versions use semantic versioning
(0.x while the boundary stabilizes).

## [Unreleased]

### Added
- Repeat consistency as an admission rule: `LabelConfirmation.Decide(…, repeats:)` takes the repeats made of the decision
  (`RepeatConsistency.Pair`), and a label whose confirming judge did not hold its own answer on one
  (`RepeatRecord.Consistent` false) is trusted at most `LabelAdmission.UnsteadyTrustCap` (whatever the trust basis) or,
  with `RefuseUnsteady`, kept out — the next admitted judge stands, or `NotAdmitted` says why.
- `TrainingSetSelector.Select(labels, policy)`: the training set picked from the labels that stand, by a
  `TrainingSetPolicy` — at most N labels per unit (`MaxPerUnit`, near-duplicates), per slice (`MaxPerSlice`) and in all
  (`MaxLabels`, shared out between slices in proportion after the `MinPerSlice` floors). Each limit thins a group by the
  labels' hash order, so the same labels and policy give the same set. A cap rebalances the set; with `Compensate` the
  labels kept carry their keep probability (`ConfirmedLabel.SelectionProbability`, `Weight` divides by it) and still
  count for everything they stand for. The manifest records what was offered, what was left out by reason, and whether
  the set was compensated.
- `MLoopPredictionRequest.Experiment` / `MLoopImagePredictionRequest.Experiment` (MLoop `POST /predict?experiment=`): a
  candidate experiment answers the same input while production keeps serving — the shadow tier's prediction. The result
  names the experiment that answered (`MLoopPredictionResult.Model`); `HttpMLoopClient` refuses an answer from another
  model or one that names none, which is what a server that does not read `experiment` returns. Nothing changes without it.
- A split key for the held-out split: `LabelConfirmation.Decide` / `Settle` take `splitKey` (the same one at both calls),
  and `LabelSplit.Assign` deals by it instead of the decision id — every decision with one key (the same normalized
  input, document or session) falls in one share, and q is the probability of the key. Without a key the deal is
  unchanged. `ConfirmedLabel.SplitKey` / `Unit` carry it; `LabelSplit.KeyKind` names what the keys are, and
  `TrainingSetManifest.SplitKeyKind` / `Units` record it (`TrainingSets.Compare` reports a different kind of key as a
  changed split). `TrainingSets.Summarize` counts labels and independent units of each share and warns of a key with
  labels in both shares or a held-out share with too few units. Keep a key inside one slice.

## [0.13.0] - 2026-10-27

### Added
- Held-out labels for measuring the learning tier: `LabelSplit` (a share of decisions, overall or per slice, under 1)
  passed to `LabelConfirmation.Decide` / `Settle` sets `ConfirmedLabel.Share` (`LabelShare.Training` / `Evaluation`)
  and `ShareProbability` q, fixed by a hash of the decision id — every label of a decision lands in the same share.
  `Weight` is now trust / (π · q) (unchanged without a split: q = 1). `LabelShareSink<T>` routes each share to its
  sink; `MLoopFeedbackSink<T>` refuses a held-out label, so a model is never trained on what it is measured against.
- `LabelConfirmation.Supersedes(previous, next)`: whether a later label for a decision replaces the one already sent
  — a consensus over any one judge, otherwise the more trusted (on a tie, measured over default trust, then the later).
  MLoop counts the most recent feedback of a prediction, so a less trusted label sent later would win there.
- `LabelAdmission` (passed to `Decide` / `Settle`): which confirmations become labels — `Qualified` (a judge that may
  confirm at all, e.g. a teacher whose must-catch recall clears the floor), `DefaultTrustCap` (an unmeasured judge's
  default trust held under a cap) and `MinimumTrust`. A judge kept out cannot confirm; with none admitted, the new
  `ConfirmationOutcome.NotAdmitted` says why.
- `TrainingSetManifest` and `TrainingSets.Manifest` / `Compare`: what a training run learned from — label counts by
  confirming judge, trust basis, slice and value, the time span, the held-out split and a SHA-256 of the contents —
  and what changed between two runs. `LifecycleRequest.TrainingSet` saves it with the run.
- `IMLoopClient.EmbedTextsAsync(MLoopTextEmbeddingRequest)`: texts to sentence vectors with a text encoder installed on
  the MLoop server (MLoop 0.46+ `POST /embed` with `{"texts": […]}`) — for comparing texts by meaning, in Korean and
  other languages. `MLoopEncoder` gains `Kind` (`image` / `text`) and `TokenizerSha256` (a text encoder's tokenizer,
  part of the space's identity); `MLoopEmbedding` gains `Truncated` (texts longer than the encoder reads).
  **Breaking for implementers**: a class implementing `IMLoopClient` itself (a test double) adds the method.
- `MLoopEmbeddingRequest.Patches`: `EmbedAsync` also returns each image's patch vectors (MLoop 0.46+
  `POST /embed?patches=true`) — `MLoopEmbedding.Grid` (`MLoopPatchGrid`) and `Patches` (`MLoopImagePatches`: the crop
  the grid covers in the image's own pixels, and one vector per patch, row by row). For a local difference one vector
  per image averages away, and where it is. A server that answers without patches is an error, not an empty grid.
- `MLoopEncoder.Preprocessor`: the revision of how MLoop turned the images into the encoder's input (MLoop 0.46+,
  `null` before). Vectors are comparable only when `Id`, `Sha256` and `Preprocessor` all match — the same weights give
  other vectors when it changes.
- `MLoopPredictionResult.Model` and `MLoopForecastResult.Model` (`MLoopModel(ExperimentId, Sha256)`): the MLoop model
  that answered, read from the response's `experimentId` and `modelSha256`. The experiment id names a model inside one
  MLoop project only; the SHA-256 is the same model wherever it is served, as an embedding's `MLoopEncoder.Sha256` is.
  `null` from a server that reports neither.
  Code that constructs either result recompiles unchanged (the parameter is optional); a binary built against 0.12
  must be rebuilt.

- Promotion gates: `ModelLifecycle`'s review stage is an `IPromotionGate` (`PromotionCandidate` →
  `PromotionVerdict`: promote, refuse or wait). `ReviewPromotionGate` is the person's review (what the `IReviewStore`
  constructor wires — unchanged behaviour); `EvidencePromotionGate` promotes a candidate shown no worse than production
  on held-out labels — the paired interval's upper end within a registered `EvidenceCriteria.Margin`, enough rows, and
  must-catch recall no lower than production's when `MustCatch` is named — and refuses otherwise. `LifecycleRun`
  gains `Metrics`, `Warnings` and `ProvenNoWorse`.
- `IMLoopClient.EvaluateAsync(MLoopEvaluationRequest)` (MLoop `POST /evaluate`: metrics, `MLoopConfusion` with
  `Recall(classes)`, and the paired comparison `against` another model) and `RollbackAsync(model, to)` (MLoop
  `POST /rollback`). **Breaking for implementers**, as `EmbedTextsAsync` above.
- `ShadowPromotionGate`: the candidate runs as a shadow version of the learning tier and is promoted once the trust
  lifecycle has released it in every slice it is watched in (from the standings the consumer keeps — with
  `AutoRelease` off, each release waited for a person); refused when it has not within `maximumShadow` of training.
- `IModelLifecycle.RollBackAsync(runId, reason)`: puts production back to exactly what the run's promotion replaced
  (`LifecycleRun.ReplacedExperimentId`, read before promoting), only while the run's model is still in production;
  the run becomes `rolled_back` (`LifecycleStage.Rollback`). `RollbackRule.Demands(before, change)` says when the
  ledger demands it — the same version tightened from active or stable to shadow. `LifecycleRun.TrainedAt`,
  `PromotionCandidate.TrainedAt`, `MLoopModelInfo.ExperimentId` (read from `GET /info`). **Breaking for implementers**
  of `IModelLifecycle`.
- A recheck pool for judges that come when they come (a person, an expensive model in spare time): `PoolItem` (no room
  for any tier's answer), `IRecheckPoolStore` with `InMemoryRecheckPoolStore` / `JsonlRecheckPoolStore`,
  `RecheckPool.PresentAsync` (an order hashed from the decision and the session, a hold per reviewer) and
  `RecheckPool.AccountAsync` — a closed window's reached items as `RecheckRecord`s at π × r, r the reach of their tier
  and slice (`PoolReach`); unreached and expired items are left out, a pass (`PoolResponseKind.Skipped`) counts as the
  tier above failing. `SessionMix` mixes planted items (`PoolItemKind.Plant`), repeats (after `MinimumRepeatGap`) and
  second opinions into a session at set shares, shuffled with the rechecks (`PoolPresentation.Purpose` /
  `PoolResponse.Purpose` — a recheck answered once per item, the rest once per judge); `PoolAnswers` turns one answer
  into the response to record, the blind `RecheckResult` for `PlantRecords` / `RepeatConsistency` /
  `DoubleCheckMeasure`, and the draw (π × r) a label is confirmed with.
- `RetrainPolicy.Decide(RetrainSignals, now)`: whether to start a retraining run — enough new learning labels
  (`MinimumNewLabels`, by Kish's effective sample `RetrainSignals.EffectiveSample`), or a stale or demoted model with
  any new label; never with a run in flight, never within `MinimumInterval` of the last one — with the reason.

### Changed
- **Breaking**: the trust lifecycle's transition record `HoneAI.Trust.LifecycleStep` is renamed `StandingChange` —
  it shared its name with the model lifecycle's `HoneAI.LifecycleStep`, so code using both namespaces could not name
  either without qualifying it. Rename the type where you read `TrustLifecycle.Release` / `Tighten` results or
  `SliceOutcome.Step`; nothing else changes.
- `HoneAI.Agents` depends on `IronHive.Agent` 0.75.0 (was 0.71.0).

## [0.12.0] - 2026-10-24

### Added
- `IMLoopClient.PredictImagesAsync(MLoopImagePredictionRequest)`: classifies images by their contents against an
  MLoop image-classification model (MLoop 0.45+ `POST /predict` with `{"images": [...]}`) — one traced prediction per
  image, in the order sent, with MLoop's confidence. The client no longer needs a path the server can read.
- `IMLoopClient.EmbedAsync(MLoopEmbeddingRequest)`: images to vectors with an encoder installed on the MLoop server
  (MLoop 0.44+ `POST /embed`), with the encoder that made them (`MLoopEncoder`: id, version, SHA-256, dimension,
  normalized) — the key under which vectors are comparable.

### Changed
- **Breaking** for a class that implements `IMLoopClient` itself (a test double, another transport): it has the two
  methods above to implement. Callers are unaffected.

## [0.11.0] - 2026-10-23

### Added
- `JsonlProvenanceSink.ReadFromAsync(TextReader)` and `ReadFromAsync(Stream)` read provenance JSONL that is not
  a local file — a downloaded blob, a database column, an HTTP response — with the same rules as the sink's own
  file: lines written by 0.2.x read as the current contract, and a line cut short mid-append is skipped.
  `ReadAsync()` on a sink is now a thin wrapper over the same reader, so the rules cannot diverge between the two.

## [0.10.0] - 2026-10-23

### Added
- `MLoopTrainRequest` carries the rest of MLoop's `POST /train` options — `Balance`, `TimeLimitSeconds`, `Metric`,
  `TestSplit`, `MaxRows` — and `HttpMLoopClient` sends each only when it is set, so an unset one leaves the choice to
  the project's `mloop.yaml`. An application can now train through `IMLoopClient` exactly as it did with
  `mloop train --balance auto --time 120 --metric auc`.
- `MLoopJob.Promoted`, `KeptInStagingReason` and `StatusMessage`, read from `GET /jobs/{id}` (MLoop 0.42 and later):
  whether MLoop put the experiment in production, why it kept it in staging, why a job failed. `Promoted` is `null`
  when the server does not say — unknown, not «no».
- `ModelLifecycle` records why a training job failed (`StatusMessage`), and notes at evaluation when MLoop promoted an
  experiment the run asked to keep in staging (a server older than 0.42 ignores the request).
- `MLoopJob.Warnings`, read from `GET /jobs/{id}`'s `warnings`: MLoop's diagnostics about the trained model, such as a
  held-out score so near-perfect it may come from a leaked column. `null` from a server that does not report them.
  `ModelLifecycle` puts them in the evaluation step, in front of the reviewer.

### Changed
- The MLOps persona's rule against declaring normalization gives MLoop's current reason: its search scales features
  ahead of linear trainers itself, and trees do not need it.

## [0.9.0] - 2026-10-22

### Changed
- **Breaking: human review is stored state, and a lifecycle run is resumable.** `IHitlGate` awaited a decision in
  process memory, and `ModelLifecycle.StartAsync` held one call open from training through a review that can take
  days, keeping the run only in memory — a restart, or a review decided in another process (a queue worker trains, a
  web request approves), lost both. Now:
  - `IReviewStore` replaces `IHitlGate`: `OpenAsync` (idempotent — returns the review already open),
    `DecideAsync` (the first decision stands; `null` for an unknown id), `GetAsync`, `ListPendingAsync`; a
    `ReviewRecord` carries the subject, when it opened and when it was decided. `WaitForDecisionAsync` polls a
    store for a caller that holds a process for the review anyway. Reference implementations
    `InMemoryReviewStore` and `JsonlReviewStore` (an append-only event file processes on one machine can share)
    replace `InMemoryHitlGate`.
  - `IModelLifecycle.StartAsync` drives a run to its open review and returns (`waiting_review`) instead of
    waiting; `ResumeAsync(runId)` continues it — promotes on an approval, settles on a rejection, stays waiting
    while undecided, and polls the recorded job again for a run interrupted while training. `LifecycleRun` carries
    the request, review id, job id, experiment id and `UpdatedAt`; it is saved after every step to an
    `ILifecycleStore` (`InMemoryLifecycleStore` by default, `JsonlLifecycleStore` on file).
  - Migration: `new ModelLifecycle(mloop, hitl, sink)` → `new ModelLifecycle(mloop, reviews, runs, sink)`; where a
    reviewer called `gate.Submit(id, decision)`, call `reviews.DecideAsync(id, decision)` and then
    `lifecycle.ResumeAsync(runId)` (the review's `Subject` is the run id when the lifecycle opened it).

### Fixed
- A JSONL file whose last line a crash cut short lost the next line appended to it — glued to the torn one — and
  `JsonlProvenanceSink.ReadAsync` stopped at the torn line. Appends now start on a fresh line and reads skip a
  torn one.

### Security
- `HoneAI.Agents` depends on `IronHive.Agent` 0.71.0 (was 0.3.0). The old engine brought in
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 transitively (High, GHSA-2m69-gcr7-jv3q); the new dependency graph has no
  known advisories. `Microsoft.Extensions.AI` and `.Abstractions` move to 10.11.0 and
  `Microsoft.Extensions.Logging.Abstractions` to 10.0.13 — the floors the new engine declares.

## [0.8.0] - 2026-10-22

### Added
- `MLoopTrainRequest.Promote` — `false` asks MLoop to keep the trained experiment in staging (`"promote": false` on
  `POST /train`, MLoop 0.42 and later) for the caller to promote with `IMLoopClient.PromoteAsync`.

### Fixed
- `ModelLifecycle` trains with `Promote = false`. Its run is train → review → promote, but MLoop promoted a better
  model as soon as training finished, so it served production before the review had decided — and a rejected review
  left it there. Against an MLoop that does not know the field the old behaviour remains.

## [0.7.0] - 2026-10-22

### Added
- **Consensus judgment.** A decision that waited (`ConfirmationOutcome.NeedsConsensus`) is settled by two or more
  judges together: `ConsensusJudgment<T>` (the value, the participants, when, a reference to the grounds compared)
  goes to `LabelConfirmation.Settle`, which makes the label — at the decision's π, by the same rules as `Decide`;
  every participant sits above the learner, two or more distinct, or it is refused. Its trust is
  `LabelTrustDefaults.Consensus` (default 1 — a consensus is what teachers are measured against), recorded as
  `LabelTrustBasis.Consensus`; `ConfirmedLabel.Participants` names who took part, and the MLoop sink's source reads
  `llm+person-a (consensus)`.
- **People on one rung, measured one by one.** A rung of people is one tier (the ladder's cost ranks are
  distinct), but each person tires, disagrees and is right at their own rate. `JudgeIdentity(TierId, JudgedBy)`
  names one judge within a tier; a judge reports who answered with `JudgeVerdict.Answer(..., judgedBy:)` (a
  pseudonymous key — `ReviewDecision.ReviewerKey`), and it travels onto `PredictionProvenance.JudgedBy`,
  `PlantRecord.JudgedBy` and `ConfirmedLabel.JudgedBy`. `TeacherLedger.Compute(..., judgedBy:)` reads one person's
  cell (none: the rung pooled); plant stores record once per plant, tier and judge; a consensus's participants are
  judges, so two people on one rung are a consensus.
- **Double checks.** `DoubleCheckMeasure.Pair` records one decision judged blind by two judges (unordered,
  scored against a known answer when there is one); `Compute` gives the pair's `DoubleCheckCell` — agreement,
  Cohen's κ on the must-catch call (the spread of judges of one kind), and on known answers each judge's error
  rate, how far their errors coincide beyond independence (`ErrorOverlap`, with a hypergeometric
  `ErrorOverlapP`) and the must-catch items both missed.
- **Repeat consistency.** `RepeatConsistency.Pair` records one input judged twice, blind, by one judge (the
  second may be asked under another decision id); `Compute` gives the judge's `RepeatCell` — consistency,
  must-catch flips, and on known answers the split of its errors into `SystematicErrorRate` (repeated wrong
  answers) and `NoiseRate` (answers that moved), with `AccuracyWhenConsistent`.
- **Latent truth (experimental, `HONEAI001`).** `LatentTruth.Estimate` estimates each item's truth and each
  judge's confusion matrix together from who answered what (Dawid–Skene by expectation–maximisation), with items
  of known truth held fixed. Refuses an unidentified set — fewer than three judges and nothing known. A judge with
  a lean shows it as mass off the diagonal, and a majority that leans no longer outvotes a judge that does not.
- **Hierarchical shrinkage.** `LedgerShrinkage.Agreement` shrinks each slice's agreement toward its group's and
  the groups' toward the whole (empirical Bayes, the between-slice spread by the method of moments) — a small
  slice borrows strength as far as its siblings are alike. For ordering and reporting only; releases still rest
  on a slice's own rechecks.

### Changed
- **Breaking:** `LabelConfirmation.Decide`'s `teacherCell` lookup takes a `JudgeIdentity` instead of a tier id, so
  a label one person confirmed is trusted by that person's plants. Migrate: `id => cellOf(id)` →
  `judge => cellOf(judge.TierId)` keeps the per-tier behaviour; read `judge.JudgedBy` to go per person.

## [0.6.0] - 2026-10-21

### Added
- **Confirmed labels.** `LabelConfirmation.Decide` turns what tiers above a learning tier answered into a
  `ConfirmedLabel<T>` — or says why not (`ConfirmationOutcome`): only a tier above confirms; a settled
  decision is a label only when drawn for recheck, at its draw's π (a decision that went up: π = 1); judges
  above disagreeing on a must-catch value wait for consensus; trust is the teacher ledger's measured
  accuracy, else the kind's default (`LabelTrustDefaults`, recorded as `LabelTrustBasis.Default`). The
  label's `Weight` is trust / π.
- **Sinks.** `IConfirmedLabelSink<T>` delivers a label to an engine; `MLoopFeedbackSink<T>` records it as
  weighted feedback on the ML tier's MLoop prediction, with the confirming tier as the source;
  `MLoopFeedbackSink.ReferenceTo(prediction)` makes the reference a label carries to it.
- `IMLoopClient.RecordFeedbackAsync(MLoopFeedback)` (`POST /feedback` with source and weight), and
  `MLoopPredictionResult.PredictionId` — the id MLoop logged a prediction under, no longer mixed into the
  outputs.
- **Retraining on confirmed labels.** `LifecycleRequest.Feedback` makes a `ModelLifecycle` run first build a
  retraining set from the model's feedback (`IMLoopClient.CreateSampleAsync` — MLoop `POST /samples`, the Export
  stage) and train on the data with it merged in, weighted by the set's weight column.
  `MLoopTrainRequest` gains `AdditionalDataPaths` and `Weight`. Needs an MLoop server at 0.41.0 or later
  (`POST /samples`, and `weight` · `additionalDataFiles` on `POST /train`).

### Changed
- `IMLoopClient` gains `RecordFeedbackAsync` and `CreateSampleAsync` — an implementation of the interface adds them.

### Fixed
- `ModelLifecycle` recorded any failure as a failed Promote stage — a training run MLoop refused read as a promotion
  failure. The failure is recorded at the stage that threw.

## [0.5.0] - 2026-10-21

### Added
- **Closing a window.** `TrustController.Close(version, now, slices, policy)` judges every tier in
  every slice, derives the rate each needs, shares each pool — active tiers' rechecks first, then
  stable tiers by possible misses (u·C/(1 − u) from the window, an unmeasured tier first) — and
  issues the snapshot. A release awaiting approval keeps the old standing in the snapshot
  (`ControllerStep.AwaitingApproval`); tightening is committed at once. The README walks the loop.
- **Abstaining on the least confident answers — only where it works.** `ConfidenceAbstain.Quantile`
  issues a tier's wanted abstain quantile only when its window shows the confidence ranks mistakes
  above chance (the confidence-rank AUC's Hanley–McNeil lower bound over 0.5, on effective agreed
  and disagreed counts) and the tier is calibrated (`SliceWindow.Calibrated`); `TrustController`
  applies it to `SliceWindow.AbstainQuantile`. A confidence that runs against correctness never
  earns it.
- **What a standing inherits.** `SliceStanding.Refined(parent, policy)` — a slice cut from an old one
  by a refined key inherits its standing and dwell, a stable parent's child coming back as active one
  window short of stable. `LiveSignals.VersionProvenNoWorse` — a tier's new version shown no worse on
  the same weighted evaluation set (MLoop's `evaluate --against`) starts active instead of in
  shadow, never above where the old version stood.
- **Recheck probabilities from the promise.** `RecheckRate.Solve(targets, traffic)` derives the rate a
  stable tier needs in a slice from what the consumer promised (`TrustTargets` — agreement p0,
  detectable drop δ, must-catch miss limit m, deadline D, α, power, floor and ceiling) and what the
  slice sees in one window (`SliceTraffic` — decisions, must-catch decisions caught). The miss term
  ln(1/α)·(1 − m)/(m·C) is what zero drawn misses need to bound the conditional miss rate by m; the
  agreement term is the fixed-sample size of the release test over the window's decisions. The
  result says which term set it (`RateTerm`) and whether it hit the ceiling — a slice where risk is
  rare stays with the tier above.
- **The lifecycle of a tier in a slice.** `TrustLifecycle.Release(standing, window, teacherQualified,
  signals, policy, now)` judges a closed window: shadow → active on one window that meets every
  condition (minimum dwell, effective sample, agreement lower bound ≥ p0 + ε, miss-rate upper bound
  ≤ m, a qualified teacher, the tier above answering), active → stable on `StableWindows` in a row.
  `TrustLifecycle.Tighten` judges a running cell at any time: a new tier version → shadow; a miss
  rate shown above m or agreement shown under p0 − ε → shadow; a seen miss, agreement shown under
  p0 or drifted inputs → stable falls back to active. Every step says why (`LifecycleStep.Reason`).
  Releases wait for a person (`AwaitsApproval`) unless `LifecyclePolicy.AutoRelease` is on;
  tightening never waits.
- **Issuing the snapshot.** `PolicySnapshotIssuer.Issue(version, now, slices, policy)` turns committed
  standings and pool grants into the `PolicySnapshot` sites judge by: a shadow tier without rates, an
  active tier's promise knob at `IssuePolicy.ActiveRate`, a stable tier's at its grant, other knobs
  beside it, soft and hard expiry from the issue time — and `RateSetBy` on every entry, so "why this
  π" is answered from the snapshot. The promise knob (`uniform` by default) cannot be set by hand.
- **Staleness from MLoop's drift.** `IMLoopClient.GetDriftAsync(MLoopDriftRequest)` reads MLoop's
  `GET /drift` (`HttpMLoopClient`) into `MLoopDrift` — measured or not and why, every input column's
  PSI and level (a free-text column as its length and unseen-word share, MLoop 0.40+), the answers'
  drift, and one report per slice value. `StalenessWatch.Drifted(drift, level, sliceValue)` is the
  `LiveSignals.Drifted` a stable tier tightens on; a window MLoop could not measure is no evidence
  and does not read as drift, and a level this client does not know reads as moved.
  **Breaking:** an own `IMLoopClient` implementation needs the new member.
- `LedgerCell.MissRateLower` — the miss rate's lower bound, what demotion is judged on (a failed
  recheck counts as no miss here).
- **Sharing a pool of rechecks.** `RecheckBudget.Allocate(budget, requests, floor)` keeps every
  slice's floor first (a settled decision is never beyond recheck, even when the floors overrun the
  pool — `OverBudget`), then funds slices in order of their possible misses, an unmeasured slice
  first. A slice the pool could not fund is marked (`RateTerm.Budget`) and cannot meet its release
  conditions — the shortfall leans toward quality.

## [0.4.0] - 2026-10-21

### Added
- **Measuring what showing the lower answer does.** Rechecks are blind by default because the
  measurement literature says so; `TieredRouter.RecheckShownAsync(input, decisionId, tierId,
  lowerAnswer)` judges the same decision shown the lower answer (under
  `JudgeContext.LowerAnswerHint`, for a tier that accepts hints), and `AnchoringMeasure.Pair` /
  `Compute` turn blind–shown pairs into an `AnchoringCell`: agreement with the lower answer blind and
  shown, the moves each way with an exact McNemar p-value, and — on pairs whose answer is known —
  accuracy blind and shown with its own p-value. A shown judgment is marked
  `RecheckResult.Shown`, and `RecheckRecords.From` and `PlantRecords.From` refuse it: what the
  ledger counts stays blind.
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
  **Representativeness**: with `SelectionFitOptions.ClusterRecheckCap` (and the knob's rate), a
  meaning cluster the tilt would send more rechecks than the cap in the window is damped
  (`KnobSelection.ClusterFactors`, applied by the router through `SelectionSignal.Cluster`), and the
  mean score is refitted with the damping so the knob keeps its rate. Only the tilted share is
  capped — the uniform share is proportional to a cluster's size already.
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
    `IProvenanceSink` ② — the five surfaces at least two applications had each built for themselves.
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
- Showing applications replace their hand-written review and lifecycle code with these contracts is
  outside this repository; the contracts follow the shapes such code takes.
