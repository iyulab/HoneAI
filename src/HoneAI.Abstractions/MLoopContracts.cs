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

/// <summary>
/// Request for a forecasting-model prediction. Forecasting is horizon-based, not row-based
/// (a stateful SSA model forecasts a fixed number of steps past its training series), so it has
/// its own request shape (MLoop 0.20+ <c>POST /predict</c> forecasting contract).
/// </summary>
/// <param name="Horizon">Steps to forecast; <see langword="null"/> uses the model's trained horizon.
/// A value that differs from the trained horizon fails fast server-side.</param>
/// <param name="Model">Target model name; <see langword="null"/> uses MLoop's default model.</param>
public sealed record MLoopForecastRequest(
    int? Horizon = null,
    string? Model = null);

/// <summary>One forecast step with its native SSA confidence band (when the model provides one).</summary>
/// <param name="Value">Forecast point estimate.</param>
/// <param name="LowerBound">Band lower bound; <see langword="null"/> when the model has no band.</param>
/// <param name="UpperBound">Band upper bound; <see langword="null"/> when the model has no band.</param>
/// <param name="IntervalConfidence">Coverage level of the band (e.g. 0.95).</param>
public sealed record MLoopForecastPoint(
    double Value,
    double? LowerBound = null,
    double? UpperBound = null,
    double? IntervalConfidence = null);

/// <summary>Ordered forecast — index 0 is the first step after the training series.</summary>
public sealed record MLoopForecastResult(IReadOnlyList<MLoopForecastPoint> Points);

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
