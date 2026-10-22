using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Tests for <see cref="ModelLifecycle"/> — the train→poll→review(HITL)→promote capstone as a
/// resumable run — using a fake <see cref="IMLoopClient"/> and the real review and run stores: a run
/// starts, stops at its open review, and a later <see cref="ModelLifecycle.ResumeAsync"/> — possibly from
/// another instance over the same files — finishes it once the review is decided.
/// </summary>
public class ModelLifecycleTests : IDisposable
{
    private static readonly Func<CancellationToken, Task> NoDelay = _ => Task.CompletedTask;
    private static readonly MLoopTrainRequest Train = new("data.csv", Label: "y", Task: "binary", Model: "weld");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "honeai-lifecycle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static ModelLifecycle Lifecycle(
        FakeMLoop mloop, IReviewStore reviews, ILifecycleStore? runs = null, IProvenanceSink? sink = null)
        => new(mloop, reviews, runs, sink, pollDelay: NoDelay, runIdFactory: () => "run-1");

    [Fact]
    public async Task Start_StopsAtTheOpenReview_WithoutWaiting()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var reviews = new InMemoryReviewStore();

        var run = await Lifecycle(mloop, reviews).StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));

        Assert.Equal("waiting_review", run.Status);
        Assert.Equal(("job-1", "exp-9", "rev-1"), (run.JobId, run.ExperimentId, run.ReviewId));
        Assert.False(mloop.Trained!.Promote);                               // MLoop must not promote before the review
        Assert.Null(mloop.Promoted);
        var pending = Assert.Single(await reviews.ListPendingAsync());
        Assert.Equal(("rev-1", "run-1"), (pending.ReviewId, pending.Subject));
    }

    [Fact]
    public async Task ApprovedReview_ResumedRunPromotesAndCompletes()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(mloop, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));

        await reviews.DecideAsync("rev-1", new ReviewDecision("approve"));
        var run = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("completed", run!.Status);
        Assert.Equal(("weld", "exp-9"), mloop.Promoted);
        Assert.Equal(LifecycleStage.Promote, run.Steps[^1].Stage);
        Assert.Equal("completed", run.Steps[^1].Status);
        Assert.Equal(run, await lifecycle.GetAsync("run-1"));
    }

    [Fact]
    public async Task UndecidedReview_ResumeLeavesTheRunWaiting()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var lifecycle = Lifecycle(mloop, new InMemoryReviewStore());
        var started = await lifecycle.StartAsync(new LifecycleRequest(Train));

        var resumed = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("waiting_review", resumed!.Status);
        Assert.Equal(started.Steps.Count, resumed.Steps.Count);             // nothing re-run
        Assert.Equal(1, mloop.TrainCalls);
        Assert.Null(mloop.Promoted);
    }

    [Fact]
    public async Task RejectedReview_CompletesWithoutPromoting()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(mloop, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));

        await reviews.DecideAsync("rev-1", new ReviewDecision("reject", "metrics too low"));
        var run = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("completed", run!.Status);
        Assert.Null(mloop.Promoted);                                 // never promoted
        Assert.Equal(LifecycleStage.Review, run.Steps[^1].Stage);
        Assert.Equal("rejected", run.Steps[^1].Status);
        Assert.Equal("metrics too low", run.Steps[^1].Detail);
    }

    [Fact]
    public async Task ARunOutlivesItsProcess_AnotherInstanceOverTheSameFilesFinishesIt()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var reviewsPath = Path.Combine(_dir, "reviews.jsonl");
        var runsPath = Path.Combine(_dir, "runs.jsonl");

        // A worker starts the run and goes away.
        await Lifecycle(mloop, new JsonlReviewStore(reviewsPath), new JsonlLifecycleStore(runsPath))
            .StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1", Trigger: "schedule"));

        // Days later, a web request decides; then another process resumes.
        Assert.NotNull(await new JsonlReviewStore(reviewsPath).DecideAsync("rev-1", new ReviewDecision("approve", ReviewerKey: "r-7")));
        var resumer = Lifecycle(mloop, new JsonlReviewStore(reviewsPath), new JsonlLifecycleStore(runsPath));
        var run = await resumer.ResumeAsync("run-1");

        Assert.Equal("completed", run!.Status);
        Assert.Equal(("weld", "exp-9"), mloop.Promoted);
        Assert.Equal("schedule", run.Request!.Trigger);                     // the request survived the file
        Assert.Equal(1, mloop.TrainCalls);
        Assert.Equal("completed", (await new JsonlLifecycleStore(runsPath).GetAsync("run-1"))!.Status);
    }

    [Fact]
    public async Task ARunStoppedWhileTraining_ResumesByPollingTheRecordedJob()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var runs = new InMemoryLifecycleStore();
        // What a process that died mid-poll left: the job submitted and recorded, nothing after.
        await runs.SaveAsync(new LifecycleRun("run-1", "running", [new LifecycleStep(LifecycleStage.Train, "running", "job-1")])
        {
            Request = new LifecycleRequest(Train),
            ReviewId = "run-1",
            JobId = "job-1",
        });

        var run = await Lifecycle(mloop, new InMemoryReviewStore(), runs).ResumeAsync("run-1");

        Assert.Equal("waiting_review", run!.Status);
        Assert.Equal("exp-9", run.ExperimentId);
        Assert.Equal(0, mloop.TrainCalls);                                   // not trained again
        Assert.Contains(run.Steps, s => s.Stage == LifecycleStage.Evaluate);
    }

    [Fact]
    public async Task WithFeedback_TrainsOnTheDataAndTheRetrainingSetFromFeedback()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var lifecycle = Lifecycle(mloop, new InMemoryReviewStore());

        var run = await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1",
            Feedback: new MLoopSampleRequest(Strategy: "feedback-priority")));

        Assert.Equal("waiting_review", run.Status);
        Assert.Equal("weld", mloop.SampleAsked!.Model);                       // the training's model, when the sample names none
        Assert.Equal(new[] { ".mloop/samples/m.csv" }, mloop.Trained!.AdditionalDataPaths);
        Assert.Equal("data.csv", mloop.Trained.DataPath);
        Assert.Equal("weight", mloop.Trained.Weight);                       // trained weighted by the sample's column
        var export = run.Steps[0];
        Assert.Equal(LifecycleStage.Export, export.Stage);
        Assert.Contains("4 of 10 rows with feedback", export.Detail);
        Assert.Contains("weighted by 'weight'", export.Detail);
    }

    [Fact]
    public async Task ATrainingRunMLoopRefuses_FailsAtTrain_NotAtPromote()
    {
        var mloop = new FakeMLoop { TrainThrows = new InvalidOperationException("Data file not found") };
        var run = await Lifecycle(mloop, new InMemoryReviewStore()).StartAsync(new LifecycleRequest(Train));

        Assert.Equal("failed", run.Status);
        Assert.Equal(LifecycleStage.Train, run.Steps[^1].Stage);
        Assert.Equal("Data file not found", run.Steps[^1].Detail);
    }

    [Fact]
    public async Task FailedTraining_StopsBeforeReview()
    {
        var mloop = new FakeMLoop { FinalStatus = "failed" };
        var reviews = new InMemoryReviewStore();

        var lifecycle = Lifecycle(mloop, reviews);

        var run = await lifecycle.StartAsync(new LifecycleRequest(Train));

        Assert.Equal("failed", run.Status);
        Assert.Empty(await reviews.ListPendingAsync());                       // review never opened
        Assert.Null(mloop.Promoted);
        Assert.Equal(run, await lifecycle.ResumeAsync("run-1"));             // a settled run comes back as it is
    }

    [Fact]
    public async Task AFailedJob_SaysWhyItFailed()
    {
        var mloop = new FakeMLoop { FinalStatus = "failed", StatusMessage = "Label column 'y' not found" };

        var run = await Lifecycle(mloop, new InMemoryReviewStore()).StartAsync(new LifecycleRequest(Train));

        Assert.Equal("failed", run.Status);
        Assert.EndsWith(": Label column 'y' not found", run.Steps[^1].Detail);
    }

    [Fact]
    public async Task AServerThatPromotedAnyway_IsRecorded_BeforeTheReview()
    {
        // An MLoop older than 0.42 ignores promote=false: the experiment is in production before anyone reviewed it.
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9", ReportsPromoted = true };

        var run = await Lifecycle(mloop, new InMemoryReviewStore()).StartAsync(new LifecycleRequest(Train));

        var evaluate = run.Steps.Single(s => s.Stage == LifecycleStage.Evaluate);
        Assert.Contains("MLoop already promoted this experiment", evaluate.Detail);
    }

    [Fact]
    public async Task ProvenanceSink_RecordsEachStage()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var reviews = new InMemoryReviewStore();
        var sink = new RecordingSink();
        var lifecycle = Lifecycle(mloop, reviews, sink: sink);

        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve"));
        await lifecycle.ResumeAsync("run-1");

        // train, evaluate, review, promote all recorded under the run id.
        Assert.All(sink.Records, r => Assert.Equal("run-1", r.SubjectId));
        Assert.Contains(sink.Records, r => r.Provenance.Rationale!.StartsWith("Promote:"));
        Assert.Contains(sink.Records, r => r.Provenance.Kind == JudgeKind.Human);
    }

    // --- fakes ---

    private sealed class FakeMLoop : IMLoopClient
    {
        public string FinalStatus { get; init; } = "completed";
        public string? CompletedExperimentId { get; init; }
        public (string Model, string ExperimentId)? Promoted { get; private set; }

        public int TrainCalls { get; private set; }

        public Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken ct = default)
        {
            TrainCalls++;
            Trained = request;
            if (TrainThrows is not null) throw TrainThrows;
            return Task.FromResult(new MLoopJob("job-1", "running"));
        }

        public Task<ITracedPrediction<MLoopForecastResult>> ForecastAsync(MLoopForecastRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("lifecycle tests never forecast");

        public string? StatusMessage { get; init; }
        public bool? ReportsPromoted { get; init; }

        public Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken ct = default)
            => Task.FromResult<MLoopJob?>(new MLoopJob(jobId, FinalStatus, CompletedExperimentId,
                new Dictionary<string, double> { ["auc"] = 0.9 }, ReportsPromoted, StatusMessage: StatusMessage));

        public Task PromoteAsync(string model, string experimentId, CancellationToken ct = default)
        {
            Promoted = (model, experimentId);
            return Task.CompletedTask;
        }

        public Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken ct = default)
            => Task.FromResult<MLoopModelInfo?>(null);

        public Task<MLoopDrift> GetDriftAsync(MLoopDriftRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("lifecycle tests never read drift");

        public Task RecordFeedbackAsync(MLoopFeedback feedback, CancellationToken ct = default)
            => throw new NotSupportedException("lifecycle tests never record feedback");

        public MLoopSampleRequest? SampleAsked { get; private set; }
        public MLoopTrainRequest? Trained { get; private set; }
        public Exception? TrainThrows { get; init; }

        public Task<MLoopSample> CreateSampleAsync(MLoopSampleRequest request, CancellationToken ct = default)
        {
            SampleAsked = request;
            return Task.FromResult(new MLoopSample(".mloop/samples/m.csv", 10, 4, "y", "weight"));
        }
    }

    private sealed class RecordingSink : IProvenanceSink
    {
        public List<ProvenanceRecord> Records { get; } = new();

        public Task<ProvenanceRecord> AppendAsync(string subjectId, PredictionProvenance provenance, CancellationToken ct = default)
        {
            var record = new ProvenanceRecord(subjectId, provenance, DateTimeOffset.UnixEpoch);
            Records.Add(record);
            return Task.FromResult(record);
        }

        public async IAsyncEnumerable<ProvenanceRecord> ReadAsync(string? subjectId = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var r in Records)
                if (subjectId is null || r.SubjectId == subjectId)
                    yield return r;
            await Task.CompletedTask;
        }
    }
}
