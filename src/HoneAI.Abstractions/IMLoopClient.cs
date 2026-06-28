namespace HoneAI;

/// <summary>
/// Transport-neutral client for a running MLoop instance — unifies the HTTP
/// (U-Vision <c>MloopClassifier</c>) and MCP-stdio (SMI.AIMS <c>MLoopMcpService</c>)
/// transports the two consumers wrote separately (back-derivation §3.5 ④).
/// </summary>
/// <remarks>
/// MLoop is consumed over the wire (HTTP ∨ MCP), never duplicated as a compile-time
/// SDK reference — MLoop's SDK packages are deliberately not published to NuGet, and
/// the consumer evidence shows transport-based consumption. Phase 0 declares the
/// contract; Phase 1-④ implements the transport unification.
/// </remarks>
public interface IMLoopClient
{
    /// <summary>
    /// Run a prediction against a promoted model. The result is wrapped in
    /// <see cref="ITracedPrediction{T}"/> with <see cref="ReasoningLayer.AutoMl"/> provenance.
    /// </summary>
    Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken cancellationToken = default);

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
