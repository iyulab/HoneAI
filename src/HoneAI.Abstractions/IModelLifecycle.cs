namespace HoneAI;

/// <summary>
/// The stages of a model reliability lifecycle, in order — one-to-one with SMI.AIMS's
/// pipeline <c>StepType</c> (<c>export|init|info|train|evaluate|review|promote</c>,
/// back-derivation §3.5 ⑤).
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

    /// <summary>Human review gate (<c>review</c>, HITL — see <see cref="IHitlGate"/>).</summary>
    Review = 5,

    /// <summary>Promote the model to production (<c>promote</c>).</summary>
    Promote = 6,
}

/// <summary>Outcome of one executed lifecycle stage — the audit trail entry for a run.</summary>
/// <param name="Stage">Which stage this entry records.</param>
/// <param name="Status">Coarse status (e.g. "pending", "running", "completed", "failed", "skipped", "waiting_input").</param>
/// <param name="Detail">Optional output, log, or error detail.</param>
public sealed record LifecycleStep(LifecycleStage Stage, string Status, string? Detail = null);

/// <summary>A lifecycle run and its accumulated step trail.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Status">Run-level status (e.g. "queued", "running", "waiting_review", "completed", "failed", "cancelled").</param>
/// <param name="Steps">Ordered trail of executed/queued steps.</param>
public sealed record LifecycleRun(string RunId, string Status, IReadOnlyList<LifecycleStep> Steps);

/// <summary>Input for one lifecycle run.</summary>
/// <param name="Training">What to train (data path, label, task, model).</param>
/// <param name="ReviewId">The id the human-review gate keys on; defaults to the run id.</param>
/// <param name="Trigger">Who/what triggered the run (e.g. "manual", "auto", "schedule").</param>
public sealed record LifecycleRequest(MLoopTrainRequest Training, string? ReviewId = null, string? Trigger = null);

/// <summary>
/// Orchestrates a model reliability lifecycle — export→init→inspect→train→evaluate→
/// review(HITL)→promote — tracking each step. Generalizes SMI.AIMS's
/// <c>MLoopPipelineService</c> and U-Vision's <c>FileDatasetExporter</c> + activate
/// (back-derivation §3.5 ⑤). Dataset export format and promote policy stay in the
/// consumer adapter; the <see cref="LifecycleStage.Review"/> stage composes with
/// <see cref="IHitlGate"/>.
/// </summary>
public interface IModelLifecycle
{
    /// <summary>
    /// Run a lifecycle (train → poll → review → promote) to completion, returning the final
    /// run. The review stage awaits the HITL gate keyed by <see cref="LifecycleRequest.ReviewId"/>.
    /// </summary>
    Task<LifecycleRun> StartAsync(LifecycleRequest request, CancellationToken cancellationToken = default);

    /// <summary>The current state + step trail of a run, or <see langword="null"/> when unknown.</summary>
    Task<LifecycleRun?> GetAsync(string runId, CancellationToken cancellationToken = default);
}
