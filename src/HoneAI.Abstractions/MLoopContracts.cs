namespace HoneAI;

/// <summary>Request to run a prediction against a promoted MLoop model.</summary>
/// <param name="Features">Feature name → value map for a single prediction row.</param>
/// <param name="Model">Target model name; <see langword="null"/> uses MLoop's default model.</param>
public sealed record MLoopPredictionRequest(
    IReadOnlyDictionary<string, object?> Features,
    string? Model = null);

/// <summary>Outcome of an MLoop prediction — predicted output columns keyed by name.</summary>
/// <param name="Outputs">Predicted column name → value (label, score, probability, …).</param>
/// <param name="PredictionId">
/// The id MLoop logged the prediction under — what <see cref="IMLoopClient.RecordFeedbackAsync"/> takes to
/// record its actual value. <see langword="null"/> when the server did not log it.
/// </param>
public sealed record MLoopPredictionResult(IReadOnlyDictionary<string, object?> Outputs, string? PredictionId = null);

/// <summary>The actual value of a logged prediction (MLoop <c>POST /feedback</c>).</summary>
/// <param name="PredictionId">The id MLoop logged the prediction under (<see cref="MLoopPredictionResult.PredictionId"/>).</param>
/// <param name="ActualValue">The actual value.</param>
/// <param name="Source">Where it came from — free text.</param>
/// <param name="Weight">
/// How much the row counts in training and evaluation built from feedback (for a row reviewed with
/// probability π, 1/π); <see langword="null"/> counts 1.
/// </param>
public sealed record MLoopFeedback(string PredictionId, object ActualValue, string? Source = null, double? Weight = null);

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

/// <summary>Request for how far a production model's recent inputs and answers have moved (MLoop <c>GET /drift</c>).</summary>
/// <param name="Model">Target model name; <see langword="null"/> uses MLoop's default model.</param>
/// <param name="Rows">How many of the most recent logged predictions to compare; <see langword="null"/> for MLoop's default.</param>
/// <param name="SliceBy">An input column to report drift per value of — typically the column a slice key is built from.</param>
public sealed record MLoopDriftRequest(string? Model = null, int? Rows = null, string? SliceBy = null);

/// <summary>How far a distribution moved, by MLoop's reading of the population stability index.</summary>
public enum MLoopDriftLevel
{
    /// <summary>PSI under 0.1.</summary>
    None = 0,

    /// <summary>PSI from 0.1 to 0.25.</summary>
    Moderate = 1,

    /// <summary>PSI of 0.25 or more.</summary>
    Significant = 2,
}

/// <summary>One column's — or the answers' — drift.</summary>
/// <param name="Column">The column, or MLoop's name for the answers.</param>
/// <param name="Psi">Its population stability index against the training profile.</param>
/// <param name="Level">How MLoop reads it.</param>
public sealed record MLoopColumnDrift(string Column, double Psi, MLoopDriftLevel Level);

/// <summary>A window's drift: the inputs' columns (most moved first) and the model's answers.</summary>
/// <param name="Rows">Logged predictions compared.</param>
/// <param name="Level">The level of the most moved input column.</param>
/// <param name="Features">Every profiled input column, most moved first.</param>
/// <param name="Prediction">The answers' drift; <see langword="null"/> when the model was trained without an answer profile.</param>
public sealed record MLoopDriftReport(int Rows, MLoopDriftLevel Level, IReadOnlyList<MLoopColumnDrift> Features, MLoopColumnDrift? Prediction)
{
    /// <summary>The inputs or the answers moved at least to <paramref name="level"/>.</summary>
    public bool MovedAtLeast(MLoopDriftLevel level) => Level >= level || Prediction?.Level >= level;
}

/// <summary>
/// The drift of a production model: measured or not (and why), the overall report, and one report per
/// slice value when asked.
/// </summary>
/// <param name="Status">MLoop's status — <c>Measured</c>, <c>NoProduction</c>, <c>NoProfile</c> or <c>TooFewRows</c>.</param>
/// <param name="ExperimentId">The production experiment the window was compared against.</param>
/// <param name="Message">MLoop's one-line reading.</param>
/// <param name="Report">The overall report; <see langword="null"/> unless measured.</param>
/// <param name="Slices">Report per value of the slice column; <see langword="null"/> unless asked.</param>
public sealed record MLoopDrift(
    string Status,
    string? ExperimentId,
    string Message,
    MLoopDriftReport? Report,
    IReadOnlyDictionary<string, MLoopDriftReport>? Slices = null)
{
    /// <summary>The window was compared. Anything else is no evidence either way — not "no drift".</summary>
    public bool Measured => Status == "Measured" && Report is not null;
}
