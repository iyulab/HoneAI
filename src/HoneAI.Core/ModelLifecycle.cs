using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// Orchestrates train → (poll) → review → promote over an <see cref="IMLoopClient"/> and an
/// <see cref="IPromotionGate"/> as a resumable run: the run is saved to an <see cref="ILifecycleStore"/>
/// after every step, a gate that cannot decide yet leaves the run waiting rather than a wait in memory, and
/// <see cref="ResumeAsync"/> continues a run from where it stopped — in this process or another, after
/// a restart, days later. Consumer-specific stages (dataset export, init, inspect) stay in the consumer;
/// this capstone starts at training. A <see cref="IProvenanceSink"/> may record each stage outcome.
/// </summary>
/// <remarks>
/// The gate decides promotion: a person's review (<see cref="ReviewPromotionGate"/>, what the
/// <see cref="IReviewStore"/> constructor wires), evidence on held-out labels (<see cref="EvidencePromotionGate"/>),
/// or the consumer's own. A refusal settles the run before promote. One instance serializes the runs it resumes by
/// id; two processes resuming the same run at once is the consumer's to prevent (a queue delivers a run to one worker).
/// </remarks>
public sealed class ModelLifecycle : IModelLifecycle
{
    private readonly IMLoopClient _mloop;
    private readonly IPromotionGate _gate;
    private readonly ILifecycleStore _runs;
    private readonly IProvenanceSink? _provenance;
    private readonly Func<CancellationToken, Task> _pollDelay;
    private readonly int _maxPolls;
    private readonly Func<string> _runIdFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runLocks = new(StringComparer.Ordinal);

    /// <param name="mloop">Client for train/poll/promote.</param>
    /// <param name="reviews">Where a person's review is opened and its decision read — the gate is <see cref="ReviewPromotionGate"/>.</param>
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
        : this(mloop, new ReviewPromotionGate(reviews ?? throw new ArgumentNullException(nameof(reviews))),
            runs, provenance, pollDelay, maxPolls, runIdFactory, clock)
    {
    }

    /// <param name="mloop">Client for train/poll/promote.</param>
    /// <param name="gate">What decides whether a trained model is promoted.</param>
    /// <param name="runs">Where runs are saved between steps; defaults to this process's memory.</param>
    /// <param name="provenance">Optional sink recording each stage outcome.</param>
    /// <param name="pollDelay">Delay between job polls; defaults to 2s. Pass a no-op for tests.</param>
    /// <param name="maxPolls">Max job polls before giving up; defaults to 150.</param>
    /// <param name="runIdFactory">Run id source; defaults to a new GUID per run.</param>
    /// <param name="clock">Time source for <see cref="LifecycleRun.UpdatedAt"/>; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public ModelLifecycle(
        IMLoopClient mloop,
        IPromotionGate gate,
        ILifecycleStore? runs = null,
        IProvenanceSink? provenance = null,
        Func<CancellationToken, Task>? pollDelay = null,
        int maxPolls = 150,
        Func<string>? runIdFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _mloop = mloop ?? throw new ArgumentNullException(nameof(mloop));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
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
                {
                    return await FailAsync(run, LifecycleStage.Train,
                        $"job {job.Id} status={job.Status}" + (job.StatusMessage is { } why ? $": {why}" : string.Empty), cancellationToken)
                        .ConfigureAwait(false);
                }

                // Evaluate (metrics carried by the completed job). The run asked MLoop to keep the experiment in
                // staging; a server that ignores the request promotes on its own, and the review below no longer gates it.
                stage = LifecycleStage.Evaluate;
                var detail = DescribeMetrics(job.Metrics);
                if (job.Promoted == true)
                    detail = (detail is null ? string.Empty : detail + "; ") + "MLoop already promoted this experiment — the server ignored promote=false";
                // MLoop's warnings about the model belong in front of the reviewer who decides on it.
                if (job.Warnings is { Count: > 0 } warnings)
                    detail = (detail is null ? string.Empty : detail + "; ") + "MLoop warns: " + string.Join("; ", warnings);
                run = await RecordAsync(run with { ExperimentId = job.ExperimentId, Metrics = job.Metrics, Warnings = job.Warnings, TrainedAt = _clock() },
                    LifecycleStage.Evaluate, "completed",
                    detail, JudgeKind.Ml, cancellationToken).ConfigureAwait(false);
            }

            // Review: the gate decides — asked again on every resume until it does.
            stage = LifecycleStage.Review;
            if (run.ExperimentId is not { Length: > 0 } experimentId)
                return await FailAsync(run, LifecycleStage.Promote, "no experimentId on completed job", cancellationToken)
                    .ConfigureAwait(false);
            var model = run.Request?.Training.Model ?? "default";
            var verdict = await _gate.JudgeAsync(new PromotionCandidate(run.RunId, model, experimentId)
            {
                ReviewId = run.ReviewId ?? run.RunId,
                Metrics = run.Metrics,
                Warnings = run.Warnings,
                TrainedAt = run.TrainedAt,
                TrainingSet = run.Request?.TrainingSet,
            }, cancellationToken).ConfigureAwait(false);
            if (verdict.Decision == PromotionDecision.Wait)
            {
                return run.Status == "waiting_review"
                    ? run
                    : await SaveAsync(run with { Status = "waiting_review" }, cancellationToken).ConfigureAwait(false);
            }

            var promote = verdict.Decision == PromotionDecision.Promote;
            run = await RecordAsync(run with { ProvenNoWorse = promote && verdict.ProvenNoWorse }, LifecycleStage.Review,
                promote ? "completed" : "rejected", verdict.Reason, verdict.DecidedBy, cancellationToken, verdict.By).ConfigureAwait(false);
            if (!promote)
                return await SaveAsync(run with { Status = "completed" }, cancellationToken).ConfigureAwait(false); // completed without promotion

            // Promote.
            stage = LifecycleStage.Promote;
            // What production was, so a rollback puts back exactly that and never undoes a later promotion.
            var replaced = (await _mloop.GetInfoAsync(model, cancellationToken).ConfigureAwait(false))?.ExperimentId;
            await _mloop.PromoteAsync(model, experimentId, cancellationToken).ConfigureAwait(false);
            run = await RecordAsync(run with { ReplacedExperimentId = replaced }, LifecycleStage.Promote, "completed",
                replaced is null ? experimentId : $"{experimentId}, replacing {replaced}", JudgeKind.Ml, cancellationToken)
                .ConfigureAwait(false);
            return await SaveAsync(run with { Status = "completed" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(run, stage, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<LifecycleRun?> RollBackAsync(string runId, string reason, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var gate = _runLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
                return null;
            if (!run.Steps.Any(s => s.Stage == LifecycleStage.Promote && s.Status == "completed") || run.ExperimentId is not { } promoted)
                return await NoteAsync(run, $"{reason} — the run did not promote, so there is nothing to put back", by, cancellationToken).ConfigureAwait(false);
            if (run.Status == "rolled_back")
                return run;
            if (run.ReplacedExperimentId is not { } replaced)
                return await NoteAsync(run, $"{reason} — {promoted} was the first model, there is nothing to put back", by, cancellationToken).ConfigureAwait(false);

            var model = run.Request?.Training.Model ?? "default";
            var current = (await _mloop.GetInfoAsync(model, cancellationToken).ConfigureAwait(false))?.ExperimentId;
            if (!string.Equals(current, promoted, StringComparison.OrdinalIgnoreCase))
                return await NoteAsync(run, $"{reason} — production is now {current ?? "empty"}, not {promoted}; a later change is not undone in this run's name",
                    by, cancellationToken).ConfigureAwait(false);

            var rollback = await _mloop.RollbackAsync(model, replaced, cancellationToken).ConfigureAwait(false);
            run = await RecordAsync(run, LifecycleStage.Rollback, "completed", $"{reason} — {promoted} → {rollback.ExperimentId}",
                by is null ? JudgeKind.Ml : JudgeKind.Human, cancellationToken, by).ConfigureAwait(false);
            return await SaveAsync(run with { Status = "rolled_back" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            return run is null ? null : await NoteAsync(run, $"{reason} — rollback failed: {ex.Message}", by, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // A rollback that did not happen, on the trail; the run keeps its status.
    private Task<LifecycleRun> NoteAsync(LifecycleRun run, string detail, string? by, CancellationToken cancellationToken)
        => SaveAsync(run with { Steps = [.. run.Steps, new LifecycleStep(LifecycleStage.Rollback, "skipped", detail) { By = by }] }, cancellationToken);

    private async Task<LifecycleRun> RecordAsync(
        LifecycleRun run, LifecycleStage stage, string status, string? detail, JudgeKind kind, CancellationToken cancellationToken, string? by = null)
    {
        run = await SaveAsync(run with { Status = "running", Steps = [.. run.Steps, new LifecycleStep(stage, status, detail) { By = by }] }, cancellationToken)
            .ConfigureAwait(false);
        if (_provenance is not null)
        {
            await _provenance.AppendAsync(run.RunId,
                new PredictionProvenance { Kind = kind, JudgedBy = by, Rationale = $"{stage}:{status}" },
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
