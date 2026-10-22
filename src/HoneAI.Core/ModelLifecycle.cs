using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// Orchestrates train → (poll) → review (HITL) → promote over an <see cref="IMLoopClient"/> and an
/// <see cref="IReviewStore"/> as a resumable run: the run is saved to an <see cref="ILifecycleStore"/>
/// after every step, the review is a stored review rather than a wait in memory, and
/// <see cref="ResumeAsync"/> continues a run from where it stopped — in this process or another, after
/// a restart, days later. Consumer-specific stages (dataset export, init, inspect) stay in the consumer;
/// this capstone starts at training. A <see cref="IProvenanceSink"/> may record each stage outcome.
/// </summary>
/// <remarks>
/// Promotion happens only when the review verdict is an approval (<c>approve</c>); a rejection settles
/// the run before promote. One instance serializes the runs it resumes by id; two processes resuming the
/// same run at once is the consumer's to prevent (a queue delivers a run to one worker).
/// </remarks>
public sealed class ModelLifecycle : IModelLifecycle
{
    private static readonly HashSet<string> ApprovalVerdicts =
        new(StringComparer.OrdinalIgnoreCase) { "approve", "approved", "accept", "promote" };

    private readonly IMLoopClient _mloop;
    private readonly IReviewStore _reviews;
    private readonly ILifecycleStore _runs;
    private readonly IProvenanceSink? _provenance;
    private readonly Func<CancellationToken, Task> _pollDelay;
    private readonly int _maxPolls;
    private readonly Func<string> _runIdFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runLocks = new(StringComparer.Ordinal);

    /// <param name="mloop">Client for train/poll/promote.</param>
    /// <param name="reviews">Where the human review stage opens its review and reads the decision.</param>
    /// <param name="runs">Where runs are saved between steps; defaults to this process's memory.</param>
    /// <param name="provenance">Optional sink recording each stage outcome.</param>
    /// <param name="pollDelay">Delay between job polls; defaults to 2s. Pass a no-op for tests.</param>
    /// <param name="maxPolls">Max job polls before giving up; defaults to 150.</param>
    /// <param name="runIdFactory">Run id source; defaults to a new GUID per run.</param>
    /// <param name="clock">Time source for <see cref="LifecycleRun.UpdatedAt"/>; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public ModelLifecycle(
        IMLoopClient mloop,
        IReviewStore reviews,
        ILifecycleStore? runs = null,
        IProvenanceSink? provenance = null,
        Func<CancellationToken, Task>? pollDelay = null,
        int maxPolls = 150,
        Func<string>? runIdFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _mloop = mloop ?? throw new ArgumentNullException(nameof(mloop));
        _reviews = reviews ?? throw new ArgumentNullException(nameof(reviews));
        _runs = runs ?? new InMemoryLifecycleStore();
        _provenance = provenance;
        _pollDelay = pollDelay ?? (ct => Task.Delay(TimeSpan.FromSeconds(2), ct));
        if (maxPolls < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPolls));
        _maxPolls = maxPolls;
        _runIdFactory = runIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task<LifecycleRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => _runs.GetAsync(runId, cancellationToken);

    /// <inheritdoc />
    public async Task<LifecycleRun> StartAsync(LifecycleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = _runIdFactory();
        var gate = _runLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await SaveAsync(new LifecycleRun(runId, "running", [])
            {
                Request = request,
                ReviewId = request.ReviewId ?? runId,
            }, cancellationToken).ConfigureAwait(false);

            var stage = LifecycleStage.Export;
            try
            {
                // Export: the confirmed labels recorded as feedback become a retraining set merged into training.
                // Promotion waits for the review, so MLoop keeps the experiment in staging — left to itself it
                // promotes a better model the moment training ends, before anyone has reviewed it.
                var training = request.Training with { Promote = false };
                if (request.Feedback is { } feedback)
                {
                    var sample = await _mloop.CreateSampleAsync(feedback with { Model = feedback.Model ?? training.Model }, cancellationToken)
                        .ConfigureAwait(false);
                    run = await RecordAsync(run, LifecycleStage.Export, "completed",
                        $"{sample.Path}: {sample.LabeledRows} of {sample.Rows} rows with feedback"
                        + (sample.WeightColumn is { } w ? $", weighted by '{w}'" : string.Empty), JudgeKind.Ml, cancellationToken)
                        .ConfigureAwait(false);
                    // Rows weighted by the labels' trust / π are trained on as weighted — unless the request says otherwise.
                    training = training with
                    {
                        AdditionalDataPaths = [.. training.AdditionalDataPaths ?? [], sample.Path],
                        Weight = training.Weight ?? sample.WeightColumn,
                    };
                }

                stage = LifecycleStage.Train;
                var job = await _mloop.TrainAsync(training, cancellationToken).ConfigureAwait(false);
                run = await RecordAsync(run with { JobId = job.Id }, LifecycleStage.Train, "running", job.Id, JudgeKind.Ml, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The stage that threw: a training run MLoop refused failed at Train.
                return await FailAsync(run, stage, ex.Message, cancellationToken).ConfigureAwait(false);
            }

            return await ContinueAsync(run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<LifecycleRun?> ResumeAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var gate = _runLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.IsSettled)
                return run;
            return await ContinueAsync(run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // Drives a saved run from where its trail says it stopped: training (poll the recorded job), then
    // the review (open it, read it), then promote. Each step is saved before the next begins, so a run
    // interrupted anywhere resumes at the step it had not finished.
    private async Task<LifecycleRun> ContinueAsync(LifecycleRun run, CancellationToken cancellationToken)
    {
        var stage = LifecycleStage.Train;
        try
        {
            if (!run.Steps.Any(s => s.Stage == LifecycleStage.Evaluate))
            {
                if (run.JobId is not { } jobId)
                    return await FailAsync(run, LifecycleStage.Train, "the training job was not recorded before the run stopped", cancellationToken)
                        .ConfigureAwait(false);

                var job = await _mloop.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
                          ?? new MLoopJob(jobId, "unknown");
                job = await PollToCompletionAsync(job, cancellationToken).ConfigureAwait(false);
                if (!IsCompleted(job.Status))
                    return await FailAsync(run, LifecycleStage.Train, $"job {job.Id} status={job.Status}", cancellationToken)
                        .ConfigureAwait(false);

                // Evaluate (metrics carried by the completed job).
                stage = LifecycleStage.Evaluate;
                run = await RecordAsync(run with { ExperimentId = job.ExperimentId }, LifecycleStage.Evaluate, "completed",
                    DescribeMetrics(job.Metrics), JudgeKind.Ml, cancellationToken).ConfigureAwait(false);
            }

            // Review (HITL): open it — idempotently, a resumed run finds the one it opened — and read it.
            stage = LifecycleStage.Review;
            var reviewId = run.ReviewId ?? run.RunId;
            var review = await _reviews.OpenAsync(reviewId, run.RunId, cancellationToken).ConfigureAwait(false);
            if (review.Decision is not { } decision)
            {
                return run.Status == "waiting_review"
                    ? run
                    : await SaveAsync(run with { Status = "waiting_review" }, cancellationToken).ConfigureAwait(false);
            }

            var approved = ApprovalVerdicts.Contains(decision.Verdict);
            run = await RecordAsync(run, LifecycleStage.Review, approved ? "completed" : "rejected",
                decision.Comment, JudgeKind.Human, cancellationToken).ConfigureAwait(false);
            if (!approved)
                return await SaveAsync(run with { Status = "completed" }, cancellationToken).ConfigureAwait(false); // completed without promotion

            // Promote.
            stage = LifecycleStage.Promote;
            if (string.IsNullOrEmpty(run.ExperimentId))
                return await FailAsync(run, LifecycleStage.Promote, "no experimentId on completed job", cancellationToken)
                    .ConfigureAwait(false);
            await _mloop.PromoteAsync(run.Request?.Training.Model ?? "default", run.ExperimentId, cancellationToken)
                .ConfigureAwait(false);
            run = await RecordAsync(run, LifecycleStage.Promote, "completed", run.ExperimentId, JudgeKind.Ml, cancellationToken)
                .ConfigureAwait(false);
            return await SaveAsync(run with { Status = "completed" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(run, stage, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LifecycleRun> RecordAsync(
        LifecycleRun run, LifecycleStage stage, string status, string? detail, JudgeKind kind, CancellationToken cancellationToken)
    {
        run = await SaveAsync(run with { Status = "running", Steps = [.. run.Steps, new LifecycleStep(stage, status, detail)] }, cancellationToken)
            .ConfigureAwait(false);
        if (_provenance is not null)
        {
            await _provenance.AppendAsync(run.RunId,
                new PredictionProvenance { Kind = kind, Rationale = $"{stage}:{status}" },
                cancellationToken).ConfigureAwait(false);
        }
        return run;
    }

    private Task<LifecycleRun> FailAsync(LifecycleRun run, LifecycleStage stage, string detail, CancellationToken cancellationToken)
        => SaveAsync(run with { Status = "failed", Steps = [.. run.Steps, new LifecycleStep(stage, "failed", detail)] }, cancellationToken);

    private async Task<LifecycleRun> SaveAsync(LifecycleRun run, CancellationToken cancellationToken)
    {
        run = run with { UpdatedAt = _clock() };
        await _runs.SaveAsync(run, cancellationToken).ConfigureAwait(false);
        return run;
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
