namespace HoneAI;

/// <summary>Request to run a prediction against a promoted MLoop model.</summary>
/// <param name="Features">Feature name → value map for a single prediction row.</param>
/// <param name="Model">Target model name; <see langword="null"/> uses MLoop's default model.</param>
public sealed record MLoopPredictionRequest(
    IReadOnlyDictionary<string, object?> Features,
    string? Model = null);

/// <summary>Outcome of an MLoop prediction — predicted output columns keyed by name.</summary>
/// <param name="Outputs">Predicted column name → value (label, score, probability, …).</param>
public sealed record MLoopPredictionResult(IReadOnlyDictionary<string, object?> Outputs);

/// <summary>Request to start an MLoop training run.</summary>
/// <param name="DataPath">Path to the training dataset.</param>
/// <param name="Label">Label column; <see langword="null"/> for unsupervised tasks.</param>
/// <param name="Task">ML task type (e.g. "regression", "binary"); <see langword="null"/> to let MLoop infer.</param>
/// <param name="Model">Target model name; <see langword="null"/> uses the default model.</param>
public sealed record MLoopTrainRequest(
    string DataPath,
    string? Label = null,
    string? Task = null,
    string? Model = null);

/// <summary>Handle to an MLoop training job, including its result once completed.</summary>
/// <param name="Id">Job identifier (poll with <see cref="IMLoopClient.GetJobAsync"/>).</param>
/// <param name="Status">Coarse status (e.g. "queued", "running", "completed", "failed").</param>
/// <param name="ExperimentId">The produced experiment id, once the job has completed.</param>
/// <param name="Metrics">Evaluation metrics by name, once the job has completed.</param>
public sealed record MLoopJob(
    string Id,
    string Status,
    string? ExperimentId = null,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>Metadata for a production MLoop model.</summary>
/// <param name="Model">Model name.</param>
/// <param name="Task">ML task type, when known.</param>
/// <param name="Metrics">Evaluation metrics by name (e.g. "auc", "f1", "rSquared); empty/null when none.</param>
public sealed record MLoopModelInfo(
    string Model,
    string? Task = null,
    IReadOnlyDictionary<string, double>? Metrics = null);
