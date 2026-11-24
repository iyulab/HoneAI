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

    // A training that outlasts the run's polls (a queue behind a worker) is still going, not failed: the run stays `running` and a
    // later ResumeAsync — here by another process over the same store — picks the recorded job up where the polls stopped.
    [Fact]
    public async Task ATrainingStillRunningWhenThePollsRunOut_LeavesTheRunRunning_AndResumeFinishesIt()
    {
        var runs = new InMemoryLifecycleStore();
        var slow = new FakeMLoop { FinalStatus = "running" };

        var started = await new ModelLifecycle(slow, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 3, runIdFactory: () => "run-1")
            .StartAsync(new LifecycleRequest(Train));

        Assert.Equal("running", started.Status);
        Assert.False(started.IsSettled);
        Assert.Equal("job-1", started.JobId);
        Assert.DoesNotContain(started.Steps, s => s.Status == "failed");

        // Another process, later: the job has finished.
        var done = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var resumed = await Lifecycle(done, new InMemoryReviewStore(), runs).ResumeAsync("run-1");

        Assert.Equal("waiting_review", resumed!.Status);
        Assert.Equal("exp-9", resumed.ExperimentId);
        Assert.Equal(0, done.TrainCalls);                                    // the recorded job, not a new one
    }

    [Fact]
    public async Task ATrainingStillRunning_IsPolledAgainOnEveryResume_NotSettled()
    {
        var runs = new InMemoryLifecycleStore();
        var slow = new FakeMLoop { FinalStatus = "queued" };
        var lifecycle = new ModelLifecycle(slow, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 2, runIdFactory: () => "run-1");

        await lifecycle.StartAsync(new LifecycleRequest(Train));
        var again = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("running", again!.Status);
        Assert.Equal(1, slow.TrainCalls);
    }

    // A call that ends in an exception says nothing about the job: the server may be unreachable while the training goes on. Only a
    // job MLoop reports as failed (or does not know) settles the run — the run stays `running` and the next ResumeAsync asks again.
    [Fact]
    public async Task ATransientErrorWhilePolling_LeavesTheRunRunning_AndTheNextResumeContinuesIt()
    {
        var runs = new InMemoryLifecycleStore();
        var mloop = new FakeMLoop { FinalStatus = "running" };
        var lifecycle = new ModelLifecycle(mloop, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 1, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train));

        mloop.PollFailures.Enqueue(new HttpRequestException("connection reset"));
        var hit = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("running", hit!.Status);
        Assert.False(hit.IsSettled);
        Assert.DoesNotContain(hit.Steps, s => s.Status == "failed");
        var retry = Assert.Single(hit.Steps, s => s.Status == "retrying");   // told apart from a job that failed
        Assert.Equal(LifecycleStage.Train, retry.Stage);
        Assert.Contains("connection reset", retry.Detail);

        mloop.FinalStatus = "completed";
        mloop.CompletedExperimentId = "exp-9";
        var done = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("waiting_review", done!.Status);
        Assert.Equal("exp-9", done.ExperimentId);
        Assert.Equal(1, mloop.TrainCalls);                                   // the recorded job, not a new one
    }

    [Fact]
    public async Task ATransientErrorDuringStart_LeavesTheRunRunning_NotFailed()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        mloop.PollFailures.Enqueue(new HttpRequestException("no route to host"));

        var run = await Lifecycle(mloop, new InMemoryReviewStore()).StartAsync(new LifecycleRequest(Train));

        Assert.Equal("running", run.Status);
        Assert.Equal("job-1", run.JobId);                                    // the submitted job is kept to resume on
    }

    [Fact]
    public async Task RepeatedTransientErrors_AreOneLineOnTheTrail_NotOnePerTick()
    {
        var runs = new InMemoryLifecycleStore();
        var mloop = new FakeMLoop { FinalStatus = "running" };
        var lifecycle = new ModelLifecycle(mloop, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 1, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train));

        for (var i = 1; i <= 4; i++)
        {
            mloop.PollFailures.Enqueue(new HttpRequestException("down " + i));
            await lifecycle.ResumeAsync("run-1");
        }

        var run = await lifecycle.GetAsync("run-1");
        var retry = Assert.Single(run!.Steps, s => s.Status == "retrying");
        Assert.Contains("down 4", retry.Detail);                             // the latest reason stands
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public async Task AnAnswerFromMLoop_IsTransientOnlyWhenTheServerSaysTryAgain(int status, bool transient)
    {
        var runs = new InMemoryLifecycleStore();
        var mloop = new FakeMLoop { FinalStatus = "running" };
        var lifecycle = new ModelLifecycle(mloop, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 1, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train));

        mloop.PollFailures.Enqueue(new MLoopClientException($"MLoop /jobs failed ({status})", (System.Net.HttpStatusCode)status));
        var run = await lifecycle.ResumeAsync("run-1");

        Assert.Equal(transient ? "running" : "failed", run!.Status);
    }

    // The contract on IMLoopClient names a bare TimeoutException as no answer, which is what a client with no HTTP underneath
    // can throw.
    [Fact]
    public async Task ABareTimeoutWhilePolling_IsNoAnswer()
    {
        var runs = new InMemoryLifecycleStore();
        var mloop = new FakeMLoop { FinalStatus = "running" };
        var lifecycle = new ModelLifecycle(mloop, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 1, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train));

        mloop.PollFailures.Enqueue(new TimeoutException("the store did not answer in time"));
        var run = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("running", run!.Status);
        Assert.Contains(run.Steps, s => s is { Status: "retrying" });
    }

    [Fact]
    public async Task ABugInTheClient_StillFailsTheRun()
    {
        var runs = new InMemoryLifecycleStore();
        var mloop = new FakeMLoop { FinalStatus = "running" };
        var lifecycle = new ModelLifecycle(mloop, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 1, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train));

        mloop.PollFailures.Enqueue(new InvalidOperationException("unexpected payload"));
        var run = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("failed", run!.Status);
        Assert.Contains("unexpected payload", run.Steps[^1].Detail);
    }

    [Fact]
    public async Task ACancelledPoll_StillPropagates_AndLeavesTheRunAsItWas()
    {
        var runs = new InMemoryLifecycleStore();
        var mloop = new FakeMLoop { FinalStatus = "running" };
        var lifecycle = new ModelLifecycle(mloop, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 1, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train));

        mloop.PollFailures.Enqueue(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifecycle.ResumeAsync("run-1"));

        Assert.Equal("running", (await lifecycle.GetAsync("run-1"))!.Status);
    }

    // HttpClient's own timeout arrives as a cancellation carrying a TimeoutException: no answer, not the caller's cancel.
    [Fact]
    public async Task AClientTimeout_IsNoAnswerLikeAnyOther_ButTheCallersOwnCancelStillPropagates()
    {
        var runs = new InMemoryLifecycleStore();
        var mloop = new FakeMLoop { FinalStatus = "running" };
        var lifecycle = new ModelLifecycle(mloop, new InMemoryReviewStore(), runs, pollDelay: NoDelay, maxPolls: 1, runIdFactory: () => "run-1");
        await lifecycle.StartAsync(new LifecycleRequest(Train));

        mloop.PollFailures.Enqueue(new TaskCanceledException("the request timed out", new TimeoutException()));
        var run = await lifecycle.ResumeAsync("run-1");
        Assert.Equal("running", run!.Status);
        Assert.Contains(run.Steps, s => s is { Status: "retrying" });

        // The caller cancels while the poll is under way: the same exception shape, but it is the caller's, so it propagates.
        using var cts = new CancellationTokenSource();
        mloop.OnPoll = cts.Cancel;
        mloop.PollFailures.Enqueue(new TaskCanceledException("cancelled", new TimeoutException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifecycle.ResumeAsync("run-1", cts.Token));
        Assert.DoesNotContain((await lifecycle.GetAsync("run-1"))!.Steps, s => s.Status == "failed");
    }

    // The promotion is the step that changes production, so it is the one a retry must not repeat blindly: what it replaced is
    // taken before the call, and a call whose answer was lost is checked against production before it is made again.
    [Fact]
    public async Task ATransientErrorAtPromotion_KeepsTheApprovedRunRunning_AndTheNextResumePromotesOnce()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-old" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(mloop, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve", ReviewerKey: "r-7"));

        mloop.PromoteFailures.Enqueue((new HttpRequestException("reset"), Applied: false));
        var hit = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("running", hit!.Status);
        Assert.Contains(hit.Steps, s => s is { Stage: LifecycleStage.Promote, Status: "retrying" });
        Assert.Null(mloop.Promoted);

        var done = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("completed", done!.Status);
        Assert.Equal(("weld", "exp-9"), mloop.Promoted);
        Assert.Equal("exp-old", done.ReplacedExperimentId);
        Assert.Equal(1, done.Steps.Count(s => s is { Stage: LifecycleStage.Review, Status: "completed" }));   // the verdict is not asked twice
        Assert.Equal("r-7", done.Steps.Single(s => s is { Stage: LifecycleStage.Review, Status: "completed" }).By);
    }

    [Fact]
    public async Task APromotionWhoseAnswerWasLost_IsNotRepeated_AndStillRemembersWhatItReplaced()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9", ProductionExperimentId = "exp-old" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(mloop, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve"));

        mloop.PromoteFailures.Enqueue((new HttpRequestException("timeout"), Applied: true));   // the server promoted, the answer never came
        Assert.Equal("running", (await lifecycle.ResumeAsync("run-1"))!.Status);
        var done = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("completed", done!.Status);
        Assert.Equal(1, mloop.PromoteCalls);                                 // not promoted a second time
        Assert.Equal("exp-old", done.ReplacedExperimentId);                  // not «replacing exp-9», which would roll back to itself
        var rolled = await lifecycle.RollBackAsync("run-1", "vetoed");
        Assert.Equal("rolled_back", rolled!.Status);
        Assert.Equal("exp-old", mloop.RolledBack.Single().To);
    }

    [Fact]
    public async Task APromotionMLoopRefuses_StillFailsTheRun()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var reviews = new InMemoryReviewStore();
        var lifecycle = Lifecycle(mloop, reviews);
        await lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await reviews.DecideAsync("rev-1", new ReviewDecision("approve"));

        mloop.PromoteFailures.Enqueue((new MLoopClientException("MLoop /promote failed (422): quality gate", (System.Net.HttpStatusCode)422), Applied: false));
        var run = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("failed", run!.Status);
        Assert.Contains("quality gate", run.Steps[^1].Detail);
    }

    [Fact]
    public async Task AGateThatCouldNotReachItsEvidence_LeavesTheRunWhereItWas_AndIsAskedAgain()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var gate = new FlakyGate();
        var lifecycle = new ModelLifecycle(mloop, gate, new InMemoryLifecycleStore(), pollDelay: NoDelay, runIdFactory: () => "run-1");

        var first = await lifecycle.StartAsync(new LifecycleRequest(Train));

        Assert.Equal("running", first.Status);
        Assert.Contains(first.Steps, s => s is { Stage: LifecycleStage.Review, Status: "retrying" });
        Assert.Null(mloop.Promoted);

        var done = await lifecycle.ResumeAsync("run-1");

        Assert.Equal("completed", done!.Status);
        Assert.Equal(("weld", "exp-9"), mloop.Promoted);
    }

    [Fact]
    public async Task AJobMLoopDoesNotKnow_FailsTheRun()
    {
        var run = await Lifecycle(new FakeMLoop { JobUnknown = true }, new InMemoryReviewStore()).StartAsync(new LifecycleRequest(Train));

        Assert.Equal("failed", run.Status);
        Assert.Contains("status=unknown", run.Steps[^1].Detail);
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
        public string FinalStatus { get; set; } = "completed";
        public string? CompletedExperimentId { get; set; }
        public (string Model, string ExperimentId)? Promoted { get; private set; }

        // Calls that end in an exception, in order — one per call. `Applied` = the server did it and only the answer was lost.
        public Queue<Exception> PollFailures { get; } = new();
        public Queue<(Exception Error, bool Applied)> PromoteFailures { get; } = new();
        public int PromoteCalls { get; private set; }

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
        public Task<MLoopDataset> UploadDatasetAsync(MLoopDatasetUpload upload, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteDatasetAsync(string datasetId, CancellationToken ct = default) => throw new NotSupportedException();

        public string? StatusMessage { get; init; }
        public bool? ReportsPromoted { get; init; }
        public IReadOnlyList<string>? Warnings { get; init; }

        public bool JobUnknown { get; init; }

        public Action? OnPoll { get; set; }

        public Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            OnPoll?.Invoke();
            if (PollFailures.TryDequeue(out var failure))
                throw failure;
            return JobUnknown ? Task.FromResult<MLoopJob?>(null) : Task.FromResult<MLoopJob?>(new MLoopJob(jobId, FinalStatus, CompletedExperimentId,
                new Dictionary<string, double> { ["auc"] = 0.9 }, ReportsPromoted, StatusMessage: StatusMessage, Warnings: Warnings));
        }

        public Task PromoteAsync(string model, string experimentId, CancellationToken ct = default)
        {
            PromoteCalls++;
            if (PromoteFailures.TryDequeue(out var failure))
            {
                if (failure.Applied)
                {
                    Promoted = (model, experimentId);
                    ProductionExperimentId = experimentId;
                }
                throw failure.Error;
            }
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

    // A gate whose evidence lives behind a call that fails the first time it is asked, then promotes.
    private sealed class FlakyGate : IPromotionGate
    {
        private int _asked;

        public Task<PromotionVerdict> JudgeAsync(PromotionCandidate candidate, CancellationToken ct = default)
            => ++_asked == 1
                ? throw new HttpRequestException("evaluation endpoint unreachable")
                : Task.FromResult(new PromotionVerdict(PromotionDecision.Promote, "shown no worse", JudgeKind.Ml));
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
