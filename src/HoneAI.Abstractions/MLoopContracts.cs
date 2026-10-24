namespace HoneAI;

/// <summary>Request to run a prediction against a promoted MLoop model.</summary>
/// <param name="Features">Feature name → value map for a single prediction row.</param>
/// <param name="Model">Target model name; <see langword="null"/> uses MLoop's default model.</param>
public sealed record MLoopPredictionRequest(
    IReadOnlyDictionary<string, object?> Features,
    string? Model = null);

/// <summary>
/// Request to classify images against an MLoop image-classification model, sending the images themselves — their
/// encoded bytes (JPEG, PNG, …) — so the caller and the MLoop server need not share a disk (MLoop 0.45+ <c>POST /predict</c>).
/// </summary>
/// <param name="Images">The images, at most 64 in one request; answered in the order given.</param>
/// <param name="Model">Target model name; <see langword="null"/> uses MLoop's default model.</param>
public sealed record MLoopImagePredictionRequest(
    IReadOnlyList<ReadOnlyMemory<byte>> Images,
    string? Model = null);

/// <summary>Request to turn images into vectors with an encoder installed on the MLoop server (MLoop 0.44+ <c>POST /embed</c>).</summary>
/// <param name="Images">The images' encoded bytes, at most 64 in one request (8 with <paramref name="Patches"/>); answered in the order given.</param>
/// <param name="Encoder">The installed encoder's id; <see langword="null"/> when the server has exactly one.</param>
/// <param name="Patches">
/// Also each image's patch vectors (MLoop 0.46+ <c>?patches=true</c>): one per region of the image, for a local
/// difference one vector per image averages away, and where it is.
/// </param>
public sealed record MLoopEmbeddingRequest(
    IReadOnlyList<ReadOnlyMemory<byte>> Images,
    string? Encoder = null,
    bool Patches = false);

/// <summary>
/// Which encoder made a set of vectors — what a caller keeps beside them and compares before mixing vectors from two
/// calls: vectors that differ in <see cref="Id"/>, <see cref="Sha256"/> or <see cref="Preprocessor"/> are not in one space.
/// </summary>
/// <param name="Id">The encoder's id.</param>
/// <param name="Version">Its definition's version.</param>
/// <param name="Sha256">Its weights' SHA-256; with <paramref name="Id"/> and <paramref name="Preprocessor"/>, the identity of the vector space.</param>
/// <param name="Dimension">The length of each vector.</param>
/// <param name="Normalized">Whether the vectors are L2-normalized — cosine similarity is then their dot product.</param>
/// <param name="Preprocessor">
/// The revision of how MLoop turned the images into the encoder's input (<c>pillow/2</c>): the same weights give other
/// vectors when it changes. <see langword="null"/> from a server before MLoop 0.46, which does not report it.
/// </param>
public sealed record MLoopEncoder(string Id, string Version, string? Sha256, int Dimension, bool Normalized, string? Preprocessor = null);

/// <summary>How an encoder's patches tile the square crop it read: <see cref="Rows"/> × <see cref="Columns"/> patches of <see cref="PatchSize"/> pixels, row by row.</summary>
public sealed record MLoopPatchGrid(int Rows, int Columns, int PatchSize, int CropSize);

/// <summary>A rectangle of an image in its own pixels: left, top, right, bottom.</summary>
public sealed record MLoopImageRegion(double X0, double Y0, double X1, double Y1);

/// <summary>
/// One image's patch vectors, row by row over the grid: patch (r, c) covers the r-th of <see cref="MLoopPatchGrid.Rows"/>
/// equal bands of <see cref="Crop"/>'s height and the c-th of <see cref="MLoopPatchGrid.Columns"/> of its width.
/// </summary>
/// <param name="Crop">The part of the image the encoder read, in the image's own pixels.</param>
/// <param name="Vectors">One vector per patch, normalized as the encoder's vectors are.</param>
public sealed record MLoopImagePatches(MLoopImageRegion Crop, IReadOnlyList<float[]> Vectors);

/// <summary>
/// Vectors with the encoder that made them, one per image in the order sent — and, when patches were asked for, the
/// grid and each image's patch vectors (same order).
/// </summary>
public sealed record MLoopEmbedding(
    MLoopEncoder Encoder,
    IReadOnlyList<float[]> Vectors,
    MLoopPatchGrid? Grid = null,
    IReadOnlyList<MLoopImagePatches>? Patches = null);

/// <summary>
/// The model that answered (MLoop's <c>experimentId</c> and <c>modelSha256</c>). The experiment id names it inside one
/// MLoop project only — <c>exp-001</c> is in every project — so a record kept across projects or servers keys answers
/// by <see cref="Sha256"/>, as an embedding is keyed by its <see cref="MLoopEncoder.Sha256"/>.
/// </summary>
/// <param name="ExperimentId">The answering experiment in the server's project; <see langword="null"/> when not reported.</param>
/// <param name="Sha256">The model file's SHA-256; <see langword="null"/> from a server that does not report it.</param>
public sealed record MLoopModel(string? ExperimentId, string? Sha256);

/// <summary>Outcome of an MLoop prediction — predicted output columns keyed by name.</summary>
/// <param name="Outputs">Predicted column name → value (label, score, probability, …).</param>
/// <param name="PredictionId">
/// The id MLoop logged the prediction under — what <see cref="IMLoopClient.RecordFeedbackAsync"/> takes to
/// record its actual value. <see langword="null"/> when the server did not log it.
/// </param>
/// <param name="Model">The model that answered; <see langword="null"/> when the server reported neither part.</param>
public sealed record MLoopPredictionResult(
    IReadOnlyDictionary<string, object?> Outputs, string? PredictionId = null, MLoopModel? Model = null);

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
/// <param name="Points">The forecast steps.</param>
/// <param name="Model">The model that answered; <see langword="null"/> when the server reported neither part.</param>
public sealed record MLoopForecastResult(IReadOnlyList<MLoopForecastPoint> Points, MLoopModel? Model = null);

/// <summary>Request to start an MLoop training run.</summary>
/// <param name="DataPath">Path to the training dataset.</param>
/// <param name="Label">Label column; <see langword="null"/> for unsupervised tasks.</param>
/// <param name="Task">ML task type (e.g. "regression", "binary"); <see langword="null"/> to let MLoop infer.</param>
/// <param name="Model">Target model name; <see langword="null"/> uses the default model.</param>
/// <param name="AdditionalDataPaths">
/// Files merged after <paramref name="DataPath"/> — a retraining set from <see cref="IMLoopClient.CreateSampleAsync"/>.
/// A file without the model's weight column counts its rows 1.
/// </param>
/// <param name="Weight">The column that weights the rows; <see langword="null"/> leaves it to the project's definition.</param>
/// <param name="Promote">
/// <see langword="false"/> keeps the trained experiment in staging (MLoop <c>"promote": false</c>) for the caller to promote
/// with <see cref="IMLoopClient.PromoteAsync"/> once it has decided; <see langword="true"/> lets MLoop promote it when it beats
/// production and passes the gates.
/// </param>
/// <param name="Balance">Class balancing of the training part (MLoop <c>balance</c>: <c>auto</c>, <c>none</c> or a target ratio); <see langword="null"/> leaves it to MLoop.</param>
/// <param name="TimeLimitSeconds">The training budget; <see langword="null"/> leaves it to the project's configuration.</param>
/// <param name="Metric">The metric to optimize (MLoop's names, e.g. <c>auc</c>); <see langword="null"/> leaves it to the project, then the task.</param>
/// <param name="TestSplit">The held-out share; <see langword="null"/> leaves it to the project.</param>
/// <param name="MaxRows">Rows to sample the data down to; <see langword="null"/> trains on all.</param>
public sealed record MLoopTrainRequest(
    string DataPath,
    string? Label = null,
    string? Task = null,
    string? Model = null,
    IReadOnlyList<string>? AdditionalDataPaths = null,
    string? Weight = null,
    bool Promote = true,
    string? Balance = null,
    int? TimeLimitSeconds = null,
    string? Metric = null,
    double? TestSplit = null,
    int? MaxRows = null);

/// <summary>A retraining set to build from a model's logged predictions and their feedback (MLoop <c>POST /samples</c>).</summary>
/// <param name="Model">Target model name; <see langword="null"/> uses the default model.</param>
/// <param name="Rows">How many logged predictions to take; <see langword="null"/> for MLoop's default.</param>
/// <param name="Strategy">MLoop's sampling strategy; <see langword="null"/> for its default. <c>feedback-priority</c> takes the rows with feedback first.</param>
public sealed record MLoopSampleRequest(string? Model = null, int? Rows = null, string? Strategy = null);

/// <summary>A retraining set MLoop wrote.</summary>
/// <param name="Path">The file, relative to the MLoop project — what <see cref="MLoopTrainRequest.AdditionalDataPaths"/> takes.</param>
/// <param name="Rows">Rows written.</param>
/// <param name="LabeledRows">Of those, rows with feedback; the rest have an empty label and training drops them.</param>
/// <param name="LabelColumn">The column the feedback was written under.</param>
/// <param name="WeightColumn">The column the rows are weighted under; <see langword="null"/> when none was written.</param>
public sealed record MLoopSample(string Path, int Rows, int LabeledRows, string? LabelColumn = null, string? WeightColumn = null);

/// <summary>Handle to an MLoop training job, including its result once completed.</summary>
/// <param name="Id">Job identifier (poll with <see cref="IMLoopClient.GetJobAsync"/>).</param>
/// <param name="Status">Coarse status (e.g. "queued", "running", "completed", "failed").</param>
/// <param name="ExperimentId">The produced experiment id, once the job has completed.</param>
/// <param name="Metrics">Evaluation metrics by name, once the job has completed.</param>
/// <param name="Promoted">
/// Whether MLoop put the experiment in production; <see langword="null"/> until the job completes, or from a server that
/// does not report it. A caller that trained with <see cref="MLoopTrainRequest.Promote"/> <see langword="false"/> reads it
/// to confirm the model stayed in staging — a server older than MLoop 0.42 ignores the request and promotes.
/// </param>
/// <param name="KeptInStagingReason">Why the experiment stayed in staging, when it did.</param>
/// <param name="StatusMessage">What MLoop says about the job's status — why it failed, when it did.</param>
/// <param name="Warnings">
/// MLoop's diagnostics about the trained model — a held-out score so near-perfect it may come from a leaked column, an
/// accuracy that hides a class. Empty when there are none; <see langword="null"/> from a server that does not report them.
/// </param>
public sealed record MLoopJob(
    string Id,
    string Status,
    string? ExperimentId = null,
    IReadOnlyDictionary<string, double>? Metrics = null,
    bool? Promoted = null,
    string? KeptInStagingReason = null,
    string? StatusMessage = null,
    IReadOnlyList<string>? Warnings = null);

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
