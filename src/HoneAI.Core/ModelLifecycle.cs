using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// Orchestrates train → (poll) → review (HITL) → promote over an <see cref="IMLoopClient"/>
/// and an <see cref="IHitlGate"/>, tracking each stage. Consumer-specific stages (dataset
/// export, init, inspect) stay in the consumer; this capstone starts at training. A
/// <see cref="IProvenanceSink"/> may be supplied to record each stage outcome.
/// </summary>
/// <remarks>
/// One instance drives runs started via <see cref="StartAsync"/>. Run state is kept in memory
/// and readable through <see cref="GetAsync"/>. Promotion happens only when the review
/// verdict is an approval (<c>approve</c>); a rejection stops the run before promote.
/// </remarks>
public sealed class ModelLifecycle : IModelLifecycle
{
    private static readonly HashSet<string> ApprovalVerdicts =
        new(StringComparer.OrdinalIgnoreCase) { "approve", "approved", "accept", "promote" };

    private readonly IMLoopClient _mloop;
    private readonly IHitlGate _hitl;
    private readonly IProvenanceSink? _provenance;
    private readonly Func<CancellationToken, Task> _pollDelay;
    private readonly int _maxPolls;
    private readonly Func<string> _runIdFactory;
    private readonly ConcurrentDictionary<string, LifecycleRun> _runs = new();

    /// <param name="mloop">Client for train/poll/promote.</param>
    /// <param name="hitl">Gate for the human review stage.</param>
    /// <param name="provenance">Optional sink recording each stage outcome.</param>
    /// <param name="pollDelay">Delay between job polls; defaults to 2s. Pass a no-op for tests.</param>
    /// <param name="maxPolls">Max job polls before giving up; defaults to 150.</param>
    /// <param name="runIdFactory">Run id source; defaults to a new GUID per run.</param>
    public ModelLifecycle(
        IMLoopClient mloop,
        IHitlGate hitl,
        IProvenanceSink? provenance = null,
        Func<CancellationToken, Task>? pollDelay = null,
        int maxPolls = 150,
        Func<string>? runIdFactory = null)
    {
        _mloop = mloop ?? throw new ArgumentNullException(nameof(mloop));
        _hitl = hitl ?? throw new ArgumentNullException(nameof(hitl));
        _provenance = provenance;
        _pollDelay = pollDelay ?? (ct => Task.Delay(TimeSpan.FromSeconds(2), ct));
        if (maxPolls < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPolls));
        _maxPolls = maxPolls;
        _runIdFactory = runIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc />
    public Task<LifecycleRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);

    /// <inheritdoc />
    public async Task<LifecycleRun> StartAsync(LifecycleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = _runIdFactory();
        var reviewId = request.ReviewId ?? runId;
        var steps = new List<LifecycleStep>();

        void Set(string status) => _runs[runId] = new LifecycleRun(runId, status, steps.ToArray());
        async Task Record(LifecycleStage stage, string status, string? detail, JudgeKind kind)
        {
            steps.Add(new LifecycleStep(stage, status, detail));
            Set(status == "failed" ? "failed" : "running");
            if (_provenance is not null)
            {
                await _provenance.AppendAsync(runId,
                    new PredictionProvenance { Kind = kind, Rationale = $"{stage}:{status}" },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        Set("running");
        var stage = LifecycleStage.Export;
        try
        {
            // Export: the confirmed labels recorded as feedback become a retraining set merged into training.
            // Promotion waits for the review below, so MLoop keeps the experiment in staging — left to itself it
            // promotes a better model the moment training ends, before anyone has reviewed it.
            var training = request.Training with { Promote = false };
            if (request.Feedback is { } feedback)
            {
                var sample = await _mloop.CreateSampleAsync(feedback with { Model = feedback.Model ?? training.Model }, cancellationToken)
                    .ConfigureAwait(false);
                await Record(LifecycleStage.Export, "completed",
                    $"{sample.Path}: {sample.LabeledRows} of {sample.Rows} rows with feedback"
                    + (sample.WeightColumn is { } w ? $", weighted by '{w}'" : string.Empty), JudgeKind.Ml).ConfigureAwait(false);
                // Rows weighted by the labels' trust / π are trained on as weighted — unless the request says otherwise.
                training = training with
                {
                    AdditionalDataPaths = [.. training.AdditionalDataPaths ?? [], sample.Path],
                    Weight = training.Weight ?? sample.WeightColumn,
                };
            }

            // Train.
            stage = LifecycleStage.Train;
            var job = await _mloop.TrainAsync(training, cancellationToken).ConfigureAwait(false);
            await Record(LifecycleStage.Train, "running", job.Id, JudgeKind.Ml).ConfigureAwait(false);

            // Poll to completion.
            job = await PollToCompletionAsync(job, cancellationToken).ConfigureAwait(false);
            if (!IsCompleted(job.Status))
            {
                await Record(LifecycleStage.Train, "failed", $"job {job.Id} status={job.Status}", JudgeKind.Ml)
                    .ConfigureAwait(false);
                return Fail(runId, steps);
            }

            // Evaluate (metrics carried by the completed job).
            await Record(LifecycleStage.Evaluate, "completed", DescribeMetrics(job.Metrics), JudgeKind.Ml)
                .ConfigureAwait(false);

            // Review (HITL): wait for the human decision.
            stage = LifecycleStage.Review;
            _runs[runId] = new LifecycleRun(runId, "waiting_review", steps.ToArray());
            var decision = await _hitl.AwaitDecisionAsync(reviewId, cancellationToken).ConfigureAwait(false);
            var approved = ApprovalVerdicts.Contains(decision.Verdict);
            await Record(LifecycleStage.Review, approved ? "completed" : "rejected",
                decision.Comment, JudgeKind.Human).ConfigureAwait(false);

            if (!approved)
                return Settle(runId, steps, "completed"); // run completed without promotion

            // Promote.
            stage = LifecycleStage.Promote;
            if (string.IsNullOrEmpty(job.ExperimentId))
            {
                await Record(LifecycleStage.Promote, "failed", "no experimentId on completed job", JudgeKind.Ml)
                    .ConfigureAwait(false);
                return Fail(runId, steps);
            }
            await _mloop.PromoteAsync(request.Training.Model ?? "default", job.ExperimentId, cancellationToken)
                .ConfigureAwait(false);
            await Record(LifecycleStage.Promote, "completed", job.ExperimentId, JudgeKind.Ml)
                .ConfigureAwait(false);

            return Settle(runId, steps, "completed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stage that threw, not always Promote: a training run MLoop refused failed at Train.
            steps.Add(new LifecycleStep(stage, "failed", ex.Message));
            return Fail(runId, steps);
        }
    }

    private async Task<MLoopJob> PollToCompletionAsync(MLoopJob job, CancellationToken ct)
    {
        for (var i = 0; i < _maxPolls && !IsTerminal(job.Status); i++)
        {
            await _pollDelay(ct).ConfigureAwait(false);
            var latest = await _mloop.GetJobAsync(job.Id, ct).ConfigureAwait(false);
            if (latest is null)
                break;
            job = latest;
        }
        return job;
    }

    private LifecycleRun Fail(string runId, List<LifecycleStep> steps)
        => _runs[runId] = new LifecycleRun(runId, "failed", steps.ToArray());

    private LifecycleRun Settle(string runId, List<LifecycleStep> steps, string status)
        => _runs[runId] = new LifecycleRun(runId, status, steps.ToArray());

    private static bool IsCompleted(string status) => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
    private static bool IsTerminal(string status)
        => IsCompleted(status) || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static string? DescribeMetrics(IReadOnlyDictionary<string, double>? metrics)
    {
        if (metrics is null || metrics.Count == 0)
            return null;
        return string.Join(", ", metrics.Select(kv => $"{kv.Key}={kv.Value:0.###}"));
    }
}
