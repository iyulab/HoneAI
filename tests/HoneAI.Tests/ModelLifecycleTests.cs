using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Tests for <see cref="ModelLifecycle"/> — the train→poll→review(HITL)→promote
/// capstone — using a fake <see cref="IMLoopClient"/> and the real
/// <see cref="InMemoryHitlGate"/>, with the review decision submitted concurrently.
/// </summary>
public class ModelLifecycleTests
{
    private static readonly Func<CancellationToken, Task> NoDelay = _ => Task.CompletedTask;
    private static readonly MLoopTrainRequest Train = new("data.csv", Label: "y", Task: "binary", Model: "weld");

    private static ModelLifecycle Lifecycle(FakeMLoop mloop, IHitlGate hitl, IProvenanceSink? sink = null)
        => new(mloop, hitl, sink, pollDelay: NoDelay, runIdFactory: () => "run-1");

    [Fact]
    public async Task ApprovedReview_PromotesAndCompletes()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var hitl = new InMemoryHitlGate();
        var lifecycle = Lifecycle(mloop, hitl);

        var runTask = lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await SubmitWhenWaiting(hitl, "rev-1", new ReviewDecision("approve"));
        var run = await runTask;

        Assert.Equal("completed", run.Status);
        Assert.Equal(("weld", "exp-9"), mloop.Promoted);
        Assert.Equal(LifecycleStage.Promote, run.Steps[^1].Stage);
        Assert.Equal("completed", run.Steps[^1].Status);
    }

    [Fact]
    public async Task RejectedReview_CompletesWithoutPromoting()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var hitl = new InMemoryHitlGate();
        var lifecycle = Lifecycle(mloop, hitl);

        var runTask = lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await SubmitWhenWaiting(hitl, "rev-1", new ReviewDecision("reject", "metrics too low"));
        var run = await runTask;

        Assert.Equal("completed", run.Status);
        Assert.Null(mloop.Promoted);                                 // never promoted
        Assert.Equal(LifecycleStage.Review, run.Steps[^1].Stage);
        Assert.Equal("rejected", run.Steps[^1].Status);
    }

    [Fact]
    public async Task FailedTraining_StopsBeforeReview()
    {
        var mloop = new FakeMLoop { FinalStatus = "failed" };
        var hitl = new InMemoryHitlGate();
        var lifecycle = Lifecycle(mloop, hitl);

        var run = await lifecycle.StartAsync(new LifecycleRequest(Train));

        Assert.Equal("failed", run.Status);
        Assert.Equal(0, hitl.PendingCount);                          // review never opened
        Assert.Null(mloop.Promoted);
    }

    [Fact]
    public async Task GetAsync_ReflectsWaitingReviewState()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var hitl = new InMemoryHitlGate();
        var lifecycle = Lifecycle(mloop, hitl);

        var runTask = lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await WaitUntil(() => hitl.PendingCount == 1);

        var snapshot = await lifecycle.GetAsync("run-1");
        Assert.Equal("waiting_review", snapshot!.Status);

        hitl.Submit("rev-1", new ReviewDecision("approve"));
        await runTask;
    }

    [Fact]
    public async Task ProvenanceSink_RecordsEachStage()
    {
        var mloop = new FakeMLoop { CompletedExperimentId = "exp-9" };
        var hitl = new InMemoryHitlGate();
        var sink = new RecordingSink();
        var lifecycle = Lifecycle(mloop, hitl, sink);

        var runTask = lifecycle.StartAsync(new LifecycleRequest(Train, ReviewId: "rev-1"));
        await SubmitWhenWaiting(hitl, "rev-1", new ReviewDecision("approve"));
        await runTask;

        // train, evaluate, review, promote all recorded under the run id.
        Assert.All(sink.Records, r => Assert.Equal("run-1", r.SubjectId));
        Assert.Contains(sink.Records, r => r.Provenance.Rationale!.StartsWith("Promote:"));
    }

    private static async Task SubmitWhenWaiting(IHitlGate gate, string reviewId, ReviewDecision decision)
    {
        await WaitUntil(() => gate is InMemoryHitlGate g && g.PendingCount >= 1);
        Assert.True(gate.Submit(reviewId, decision));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Condition not met within timeout.");
    }

    // --- fakes ---

    private sealed class FakeMLoop : IMLoopClient
    {
        public string FinalStatus { get; init; } = "completed";
        public string? CompletedExperimentId { get; init; }
        public (string Model, string ExperimentId)? Promoted { get; private set; }

        public Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken ct = default)
            => Task.FromResult(new MLoopJob("job-1", "running"));

        public Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken ct = default)
            => Task.FromResult<MLoopJob?>(new MLoopJob(jobId, FinalStatus, CompletedExperimentId,
                new Dictionary<string, double> { ["auc"] = 0.9 }));

        public Task PromoteAsync(string model, string experimentId, CancellationToken ct = default)
        {
            Promoted = (model, experimentId);
            return Task.CompletedTask;
        }

        public Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken ct = default)
            => Task.FromResult<MLoopModelInfo?>(null);
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
