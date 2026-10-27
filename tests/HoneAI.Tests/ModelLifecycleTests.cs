using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Trust;
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
    public async Task MLoopsWarnings_AreInFrontOfTheReviewer()
    {
        var mloop = new FakeMLoop { Warnings = ["Near-perfect on held-out rows (auc 1.0000)"] };

        var run = await Lifecycle(mloop, new InMemoryReviewStore()).StartAsync(new LifecycleRequest(Train));

        var evaluate = run.Steps.Single(s => s.Stage == LifecycleStage.Evaluate);
        Assert.Contains("MLoop warns: Near-perfect on held-out rows", evaluate.Detail);
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

    [Fact]
    public async Task AnEvidenceGate_PromotesWithoutAPerson_AndTheRunSaysTheNewVersionWasShownNoWorse()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-2" };
        mloop.Evaluations["exp-9"] = new MLoopEvaluation("exp-9", new Dictionary<string, double>(),
            Paired: new MLoopPairedComparison("error_rate", 120, 0.1, 0.12, -0.02, -0.05, 0.0, "Indistinguishable"));
        var gate = new EvidencePromotionGate(mloop, new EvidenceCriteria { EvaluationDataPath = "eval.csv" });

        var run = await new ModelLifecycle(mloop, gate, pollDelay: NoDelay, runIdFactory: () => "run-1")
            .StartAsync(new LifecycleRequest(Train));

        Assert.Equal("completed", run.Status);
        Assert.Equal(("weld", "exp-9"), mloop.Promoted);
        Assert.True(run.ProvenNoWorse);
        Assert.Equal(0.9, run.Metrics!["auc"]);
        var review = Assert.Single(run.Steps, s => s.Stage == LifecycleStage.Review);
        Assert.Contains("shown no worse", review.Detail);

        // Not shown no worse: the run completes, production stays.
        var worse = new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-2" };
        worse.Evaluations["exp-9"] = mloop.Evaluations["exp-9"] with { Paired = mloop.Evaluations["exp-9"].Paired! with { High = 0.04 } };
        var refused = await new ModelLifecycle(worse, new EvidencePromotionGate(worse, new EvidenceCriteria { EvaluationDataPath = "eval.csv" }),
            pollDelay: NoDelay, runIdFactory: () => "run-2").StartAsync(new LifecycleRequest(Train));
        Assert.Equal("completed", refused.Status);
        Assert.Null(worse.Promoted);
        Assert.False(refused.ProvenNoWorse);
        Assert.Equal("rejected", refused.Steps[^1].Status);
    }

    [Fact]
    public async Task RollBack_PutsBackWhatThePromotionReplaced_OnlyWhileTheRunsModelIsStillInProduction()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-2" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(mloop, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve"));
        var promoted = await lifecycle.ResumeAsync("run-1");
        Assert.Equal("exp-2", promoted!.ReplacedExperimentId);
        Assert.NotNull(promoted.TrainedAt);

        var run = await lifecycle.RollBackAsync("run-1", "demoted: must-catch miss rate shown above 2%");

        Assert.Equal("rolled_back", run!.Status);
        Assert.True(run.IsSettled);
        Assert.Equal(("weld", (string?)"exp-2"), Assert.Single(mloop.RolledBack));
        var step = run.Steps[^1];
        Assert.Equal((LifecycleStage.Rollback, "completed"), (step.Stage, step.Status));
        Assert.Contains("exp-9 → exp-2", step.Detail);

        Assert.Same(run.Status, (await lifecycle.RollBackAsync("run-1", "again"))!.Status);   // once
        Assert.Single(mloop.RolledBack);
    }

    [Fact]
    public async Task ThePromotionAndTheRollback_SayWhoDecidedThem_WhenAPersonDid()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-2" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(mloop, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve", ReviewerKey: "alice"));
        var promoted = await lifecycle.ResumeAsync("run-1");

        var review = promoted!.Steps.Single(s => s.Stage == LifecycleStage.Review);
        Assert.Equal("alice", review.By);
        Assert.Null(promoted.Steps.Single(s => s.Stage == LifecycleStage.Promote).By);          // the lifecycle's own act

        var rolled = await lifecycle.RollBackAsync("run-1", "vetoed after the fact", by: "bob");
        Assert.Equal("bob", rolled!.Steps[^1].By);
        Assert.Equal(("weld", (string?)"exp-2"), Assert.Single(mloop.RolledBack));

        // A rollback nothing asked a person for carries no name.
        var automatic = Lifecycle(new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-2" }, reviews);
        await reviews.DecideAsync("rev-2", new ReviewDecision("approve"));
        await automatic.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-2"));
        await automatic.ResumeAsync("run-1");
        Assert.Null((await automatic.RollBackAsync("run-1", "demoted"))!.Steps[^1].By);
    }

    [Fact]
    public async Task AFrozenPromotion_WaitsEvenWhenTheReviewApproved_AndGoesThroughOnceTheHoldIsLifted()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var reviews = new InMemoryReviewStore();
        SliceFreeze? hold = new("operator-1", DateTimeOffset.UtcNow, "manual labels under review");
        var gate = new FreezablePromotionGate(new ReviewPromotionGate(reviews), (_, _) => Task.FromResult<SliceFreeze?>(hold));
        var lifecycle = new ModelLifecycle(mloop, gate, pollDelay: NoDelay, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve", ReviewerKey: "alice"));

        var held = await lifecycle.ResumeAsync("run-1");
        Assert.Equal("waiting_review", held!.Status);
        Assert.Null(mloop.Promoted);

        var verdict = await gate.JudgeAsync(new PromotionCandidate("run-1", "weld", "exp-9") { ReviewId = "rev-1" });
        Assert.Equal(PromotionDecision.Wait, verdict.Decision);
        Assert.Equal("operator-1", verdict.By);
        Assert.Contains("frozen by operator-1", verdict.Reason);

        // A refusal is not a release: it passes through the hold, so a rejection during the freeze stands.
        var refused = new PromotionCandidate("run-2", "weld", "exp-10") { ReviewId = "rev-2" };
        await gate.JudgeAsync(refused);
        await reviews.DecideAsync("rev-2", new ReviewDecision("reject"));
        Assert.Equal(PromotionDecision.Refuse, (await gate.JudgeAsync(refused)).Decision);

        hold = null;
        var released = await lifecycle.ResumeAsync("run-1");
        Assert.Equal("completed", released!.Status);
        Assert.Equal(("weld", "exp-9"), mloop.Promoted);
    }

    [Fact]
    public async Task RollBack_LeavesALaterPromotion_TheFirstModel_AndAnUnpromotedRunAlone()
    {
        // Production moved on after this run promoted: its rollback must not undo someone else's change.
        var moved = new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-2" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(moved, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve"));
        await lifecycle.ResumeAsync("run-1");
        moved.ProductionExperimentId = "exp-11";
        var run = await lifecycle.RollBackAsync("run-1", "vetoed");
        Assert.Equal("completed", run!.Status);
        Assert.Empty(moved.RolledBack);
        Assert.Contains("production is now exp-11", run.Steps[^1].Detail);

        var first = new FakeMLoop { CompletedExperimentId = "exp-1" };
        var firstReviews = new InMemoryReviewStore();
        var firstLifecycle = Lifecycle(first, firstReviews);
        await firstLifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await firstReviews.DecideAsync("rev-1", new ReviewDecision("approve"));
        await firstLifecycle.ResumeAsync("run-1");
        Assert.Contains("first model", (await firstLifecycle.RollBackAsync("run-1", "vetoed"))!.Steps[^1].Detail);
        Assert.Empty(first.RolledBack);

        var waiting = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var waitingLifecycle = Lifecycle(waiting, new InMemoryReviewStore());
        await waitingLifecycle.StartAsync(new LifecycleRequest(Train));
        var unpromoted = await waitingLifecycle.RollBackAsync("run-1", "vetoed");
        Assert.Equal("waiting_review", unpromoted!.Status);
        Assert.Contains("did not promote", unpromoted.Steps[^1].Detail);
        Assert.Null(await waitingLifecycle.RollBackAsync("ghost", "vetoed"));
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
        public Task<IReadOnlyList<ITracedPrediction<MLoopPredictionResult>>> PredictImagesAsync(MLoopImagePredictionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopEmbedding> EmbedAsync(MLoopEmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopEmbedding> EmbedTextsAsync(MLoopTextEmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public string? StatusMessage { get; init; }
        public bool? ReportsPromoted { get; init; }
        public IReadOnlyList<string>? Warnings { get; init; }

        public Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken ct = default)
            => Task.FromResult<MLoopJob?>(new MLoopJob(jobId, FinalStatus, CompletedExperimentId,
                new Dictionary<string, double> { ["auc"] = 0.9 }, ReportsPromoted, StatusMessage: StatusMessage, Warnings: Warnings));

        public Task PromoteAsync(string model, string experimentId, CancellationToken ct = default)
        {
            Promoted = (model, experimentId);
            ProductionExperimentId = experimentId;
            return Task.CompletedTask;
        }

        public Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string? ProductionExperimentId { get; set; }

        public Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken ct = default)
            => Task.FromResult(ProductionExperimentId is null ? null : new MLoopModelInfo(model ?? "default", "binary", ExperimentId: ProductionExperimentId));

        public Dictionary<string, MLoopEvaluation> Evaluations { get; } = new();
        public List<MLoopEvaluationRequest> EvaluationsAsked { get; } = new();

        public Task<MLoopEvaluation> EvaluateAsync(MLoopEvaluationRequest request, CancellationToken ct = default)
        {
            EvaluationsAsked.Add(request);
            var id = request.ExperimentId ?? ProductionExperimentId!;
            var evaluation = Evaluations[id];
            return Task.FromResult(request.Against is null ? evaluation with { Paired = null, Against = null } : evaluation with { Against = ProductionExperimentId });
        }

        public List<(string Model, string? To)> RolledBack { get; } = new();

        public Task<MLoopRollback> RollbackAsync(string model, string? to = null, CancellationToken ct = default)
        {
            RolledBack.Add((model, to));
            var previous = ProductionExperimentId;
            ProductionExperimentId = to ?? "exp-prev";
            return Task.FromResult(new MLoopRollback(ProductionExperimentId, previous));
        }

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
