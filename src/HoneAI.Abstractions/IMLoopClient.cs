namespace HoneAI;

/// <summary>
/// Transport-neutral client for a running MLoop instance, unifying the HTTP and
/// MCP-stdio transports behind one contract.
/// </summary>
/// <remarks>
/// <para>
/// This contract reaches MLoop over a transport (HTTP or MCP); HoneAI takes no compile-time reference to
/// MLoop's engine.
/// </para>
/// <para>
/// <b>How a failed call is read.</b> <c>ModelLifecycle</c> (HoneAI.Core) tells a call that got <i>no answer</i>
/// from one that did, by the type of the exception. No answer — the run stays where it is, its trail gets a
/// <c>retrying</c> step, and the next resume repeats the stage:
/// <see cref="System.Net.Http.HttpRequestException"/> (whatever status it carries);
/// <see cref="TimeoutException"/>; an <see cref="OperationCanceledException"/> whose inner exception is a
/// <see cref="TimeoutException"/>, while the caller's own token is not cancelled; and
/// <c>MLoopClientException</c> (HoneAI.Core) with a 5xx, 408 or 429 status. Any other exception is a verdict
/// and ends the run as failed (a cancellation of the caller's own propagates instead). An implementation that
/// reaches MLoop another way — in process, through a database — has to turn its own temporary failures into one
/// of the types above: a provider exception such as <c>DbException</c> is not recognised, transient or not.
/// </para>
/// <para>
/// The calls that create the training — the feedback sample export and <see cref="TrainAsync"/> — are never
/// repeated, because a lost answer may mean the job exists: any failure there ends the run (a client timeout
/// reaches the caller as it is, and the next resume ends the run for having no recorded job).
/// </para>
/// </remarks>
public interface IMLoopClient
{
    /// <summary>
    /// Run a prediction against a promoted model. The result is wrapped in
    /// <see cref="ITracedPrediction{T}"/> with <see cref="JudgeKind.Ml"/> provenance.
    /// </summary>
    Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classify images by their contents against an image-classification model — one traced prediction per image, in
    /// the order sent, each with <see cref="JudgeKind.Ml"/> provenance and MLoop's confidence. The images travel in the
    /// request, so the MLoop server can be on another machine.
    /// </summary>
    Task<IReadOnlyList<ITracedPrediction<MLoopPredictionResult>>> PredictImagesAsync(MLoopImagePredictionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turn images into vectors with an encoder installed on the MLoop server — the input of a nearest-neighbour
    /// judge over confirmed cases. The result names the encoder (<see cref="MLoopEncoder"/>): vectors are comparable only
    /// within one encoder's space. With <see cref="MLoopEmbeddingRequest.Patches"/>, each image's patch vectors too.
    /// </summary>
    Task<MLoopEmbedding> EmbedAsync(MLoopEmbeddingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turn texts into sentence vectors with a text encoder installed on the MLoop server (MLoop 0.46+) — for a judge
    /// that compares texts by meaning, in Korean and other languages a word-level model cannot read. The result names the
    /// encoder, its tokenizer included: vectors are comparable only within one encoder's space.
    /// </summary>
    Task<MLoopEmbedding> EmbedTextsAsync(MLoopTextEmbeddingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Run a horizon-based forecast against a promoted forecasting model (MLoop 0.20+
    /// <c>POST /predict</c> forecasting contract). The result is wrapped in
    /// <see cref="ITracedPrediction{T}"/> with <see cref="JudgeKind.Ml"/> provenance.
    /// </summary>
    Task<ITracedPrediction<MLoopForecastResult>> ForecastAsync(MLoopForecastRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Put a training file on the MLoop server (MLoop 0.52+ <c>POST /datasets</c>) and get the path to train on — for a client on
    /// another machine, which cannot write into the server's project folder. The result's <see cref="MLoopDataset.DataPath"/> is
    /// what <see cref="MLoopTrainRequest.DataPath"/> takes. The upload stays until <see cref="DeleteDatasetAsync"/> removes it (or the
    /// server's retention does).
    /// </summary>
    Task<MLoopDataset> UploadDatasetAsync(MLoopDatasetUpload upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove an upload. <see langword="false"/> when the server has none by that id; refused (the call
    /// fails with the server's reason) while a queued or running job trains on it — remove it when the job has finished.
    /// </summary>
    Task<bool> DeleteDatasetAsync(string datasetId, CancellationToken cancellationToken = default);

    /// <summary>Start a training run; returns the job handle (poll it with <see cref="GetJobAsync"/>).</summary>
    Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current state of a training job, or <see langword="null"/> when the job is unknown.
    /// On completion the returned job carries its <see cref="MLoopJob.ExperimentId"/> and metrics.
    /// </summary>
    Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Promote an experiment to production for the given model.</summary>
    Task PromoteAsync(string model, string experimentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Put production back to an earlier experiment (MLoop 0.35+ <c>POST /rollback</c>): the one the current production
    /// replaced, or <paramref name="to"/>. The change is recorded in MLoop's promotion history like a promotion.
    /// </summary>
    Task<MLoopRollback> RollbackAsync(string model, string? to = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Score an experiment on a labeled file in the MLoop project (MLoop <c>POST /evaluate</c>) — and, with
    /// <see cref="MLoopEvaluationRequest.Against"/>, the other model on the same rows, paired per row.
    /// </summary>
    Task<MLoopEvaluation> EvaluateAsync(MLoopEvaluationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// How far the production model's recent inputs and answers have moved from what it was trained
    /// on — the staleness signal a tier's standing tightens on. A window MLoop could not measure comes
    /// back with <see cref="MLoopDrift.Measured"/> false and the reason.
    /// </summary>
    Task<MLoopDrift> GetDriftAsync(MLoopDriftRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record the actual value of a logged prediction, with its source and weight — the row a retraining
    /// dataset is built from (<c>mloop sample create --model</c>).
    /// </summary>
    Task RecordFeedbackAsync(MLoopFeedback feedback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Build a retraining set from the model's logged predictions and their feedback — the file a training
    /// run takes in <see cref="MLoopTrainRequest.AdditionalDataPaths"/>.
    /// </summary>
    Task<MLoopSample> CreateSampleAsync(MLoopSampleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Production model metadata, or <see langword="null"/> when the model or transport is unavailable.</summary>
    Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken cancellationToken = default);
}
