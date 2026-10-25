namespace HoneAI;

/// <summary>
/// The stages of a model reliability lifecycle, in order:
/// <c>export | init | info | train | evaluate | review | promote</c>.
/// </summary>
public enum LifecycleStage
{
    /// <summary>Export training data to MLoop's input layout (<c>export</c>).</summary>
    Export = 0,

    /// <summary>Initialize / ensure the MLoop project (<c>init</c>).</summary>
    Initialize = 1,

    /// <summary>Profile / inspect the dataset (<c>info</c>).</summary>
    Inspect = 2,

    /// <summary>Train via AutoML (<c>train</c>).</summary>
    Train = 3,

    /// <summary>Evaluate the trained model (<c>evaluate</c>).</summary>
    Evaluate = 4,

    /// <summary>The promotion gate (<c>review</c>) — a person's review in <see cref="IReviewStore"/> by default (<see cref="IPromotionGate"/>).</summary>
    Review = 5,

    /// <summary>Promote the model to production (<c>promote</c>).</summary>
    Promote = 6,

    /// <summary>
    /// Put production back to what the run's promotion replaced (<c>rollback</c>) — when the promoted version lost its
    /// standing, or a person vetoed it after the fact (<see cref="IModelLifecycle.RollBackAsync"/>).
    /// </summary>
    Rollback = 7,
}

/// <summary>Outcome of one executed lifecycle stage — the audit trail entry for a run.</summary>
/// <param name="Stage">Which stage this entry records.</param>
/// <param name="Status">Coarse status (e.g. "pending", "running", "completed", "failed", "skipped", "waiting_input").</param>
/// <param name="Detail">Optional output, log, or error detail.</param>
public sealed record LifecycleStep(LifecycleStage Stage, string Status, string? Detail = null);

/// <summary>
/// A lifecycle run: its status, its step trail, and what it needs to continue from where it stopped —
/// the request, the training job, the experiment and the review. Saved after every step
/// (<see cref="ILifecycleStore"/>), so a run outlives the process that started it.
/// </summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Status">
/// Run-level status: <c>running</c> (a stage is under way), <c>waiting_review</c> (the gate has not decided; the
/// run continues when it does), <c>completed</c>, <c>failed</c>, <c>rolled_back</c> (promoted, then put back).
/// </param>
/// <param name="Steps">Ordered trail of executed steps.</param>
public sealed record LifecycleRun(string RunId, string Status, IReadOnlyList<LifecycleStep> Steps)
{
    /// <summary>The request the run was started with.</summary>
    public LifecycleRequest? Request { get; init; }

    /// <summary>The review the run waits on (<see cref="LifecycleRequest.ReviewId"/>, or the run id).</summary>
    public string? ReviewId { get; init; }

    /// <summary>The MLoop training job, once submitted.</summary>
    public string? JobId { get; init; }

    /// <summary>The experiment the training produced, once it completed.</summary>
    public string? ExperimentId { get; init; }

    /// <summary>When training completed — a shadow's time to earn its standing is counted from here.</summary>
    public DateTimeOffset? TrainedAt { get; init; }

    /// <summary>The experiment in production that the run's promotion replaced; null for the first model, or before promotion.</summary>
    public string? ReplacedExperimentId { get; init; }

    /// <summary>The experiment's metrics on MLoop's held-out rows, once training completed.</summary>
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }

    /// <summary>MLoop's warnings about the experiment, once training completed.</summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// The gate promoted the experiment as shown no worse than the model it replaced (<see cref="PromotionVerdict.ProvenNoWorse"/>) —
    /// the evidence the tier's new version keeps its standing on.
    /// </summary>
    public bool ProvenNoWorse { get; init; }

    /// <summary>When the run was last saved.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Whether the run has finished — <c>completed</c>, <c>failed</c> or <c>rolled_back</c>.</summary>
    public bool IsSettled => Status is "completed" or "failed" or "rolled_back";
}

/// <summary>
/// Where lifecycle runs are kept between steps. The contract is HoneAI's and the storage the
/// consumer's (a database row per run); <c>HoneAI.Core</c> carries in-memory and JSONL reference
/// implementations.
/// </summary>
public interface ILifecycleStore
{
    /// <summary>Stores <paramref name="run"/>, replacing what was stored under its id.</summary>
    Task SaveAsync(LifecycleRun run, CancellationToken cancellationToken = default);

    /// <summary>The run stored under <paramref name="runId"/>, or <see langword="null"/>.</summary>
    Task<LifecycleRun?> GetAsync(string runId, CancellationToken cancellationToken = default);
}

/// <summary>Input for one lifecycle run.</summary>
/// <param name="Training">What to train (data path, label, task, model).</param>
/// <param name="ReviewId">The id the human-review gate keys on; defaults to the run id.</param>
/// <param name="Trigger">Who/what triggered the run (e.g. "manual", "auto", "schedule").</param>
/// <param name="Feedback">
/// When set, the run first builds a retraining set from the model's feedback — the confirmed labels sinks recorded —
/// and trains on <see cref="MLoopTrainRequest.DataPath"/> with it merged in (the <see cref="LifecycleStage.Export"/> stage).
/// </param>
/// <param name="TrainingSet">
/// What the confirmed labels the run learns from are (<c>TrainingSets.Manifest</c> over the labels the consumer kept) —
/// saved with the run, so two runs can be compared by their data.
/// </param>
public sealed record LifecycleRequest(
    MLoopTrainRequest Training, string? ReviewId = null, string? Trigger = null, MLoopSampleRequest? Feedback = null,
    TrainingSetManifest? TrainingSet = null);

/// <summary>
/// Orchestrates a model reliability lifecycle — export→train→evaluate→review(HITL)→promote —
/// as a resumable run: every step is saved, the review is a stored review that may take days, and a
/// run continues from where it stopped in whichever process resumes it. Dataset export format and
/// promote policy stay in the consumer adapter.
/// </summary>
public interface IModelLifecycle
{
    /// <summary>
    /// Starts a run and drives it as far as it can go without a person: through training and
    /// evaluation to an open review (<c>waiting_review</c>), or to <c>failed</c>. It does not wait for
    /// the review — decide it in <see cref="IReviewStore"/> and call <see cref="ResumeAsync"/>.
    /// </summary>
    Task<LifecycleRun> StartAsync(LifecycleRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Continues the run stored under <paramref name="runId"/> from where it stopped: a training job
    /// that was being polled is polled again; a run waiting on its review promotes (or stops) once the
    /// review is decided, and stays <c>waiting_review</c> while it is not. A settled run comes back as
    /// it is. <see langword="null"/> when no run is stored under the id.
    /// </summary>
    Task<LifecycleRun?> ResumeAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>The current state + step trail of a run, or <see langword="null"/> when unknown.</summary>
    Task<LifecycleRun?> GetAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts production back to what the run's promotion replaced, recording <paramref name="reason"/> — the promoted
    /// version was demoted (tightening needs no one's approval), or a person reviewing it after the fact rejected it.
    /// Only while the run's experiment is still the one in production: a later promotion is not undone in its name.
    /// A run that did not promote, or promoted the first model, is left as it is with the reason recorded.
    /// <see langword="null"/> when no run is stored under the id.
    /// </summary>
    Task<LifecycleRun?> RollBackAsync(string runId, string reason, CancellationToken cancellationToken = default);
}
