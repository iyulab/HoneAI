namespace HoneAI;

/// <summary>
/// Transport-neutral client for a running MLoop instance, unifying the HTTP and
/// MCP-stdio transports behind one contract.
/// </summary>
/// <remarks>
/// MLoop is consumed over the wire (HTTP ∨ MCP), never duplicated as a compile-time
/// SDK reference — MLoop's SDK packages are deliberately not published to NuGet, so
/// transport-based consumption is the supported integration path.
/// </remarks>
public interface IMLoopClient
{
    /// <summary>
    /// Run a prediction against a promoted model. The result is wrapped in
    /// <see cref="ITracedPrediction{T}"/> with <see cref="ReasoningLayer.AutoMl"/> provenance.
    /// </summary>
    Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Run a horizon-based forecast against a promoted forecasting model (MLoop 0.20+
    /// <c>POST /predict</c> forecasting contract). The result is wrapped in
    /// <see cref="ITracedPrediction{T}"/> with <see cref="ReasoningLayer.AutoMl"/> provenance.
    /// </summary>
    Task<ITracedPrediction<MLoopForecastResult>> ForecastAsync(MLoopForecastRequest request, CancellationToken cancellationToken = default);

    /// <summary>Start a training run; returns the job handle (poll it with <see cref="GetJobAsync"/>).</summary>
    Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current state of a training job, or <see langword="null"/> when the job is unknown.
    /// On completion the returned job carries its <see cref="MLoopJob.ExperimentId"/> and metrics.
    /// </summary>
    Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Promote an experiment to production for the given model.</summary>
    Task PromoteAsync(string model, string experimentId, CancellationToken cancellationToken = default);

    /// <summary>Production model metadata, or <see langword="null"/> when the model or transport is unavailable.</summary>
    Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken cancellationToken = default);
}
