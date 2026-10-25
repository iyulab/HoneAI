using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

public class PromotionGateTests
{
    private static readonly PromotionCandidate Candidate = new("run-1", "weld", "exp-4") { ReviewId = "rev-1" };

    private static MLoopEvaluation Scored(string id, double low, double high, int rows = 200, double[][]? counts = null) => new(
        id, new Dictionary<string, double> { ["macro_accuracy"] = 0.8 },
        new MLoopConfusion(["ok", "danger"], counts is null ? [[90, 10], [5, 95]] : [counts[0], counts[1]]),
        Paired: new MLoopPairedComparison("error_rate", rows, 0.1, 0.11, (low + high) / 2, low, high, "Indistinguishable"));

    private static (EvidencePromotionGate Gate, EvaluatingMLoop MLoop) Evidence(EvidenceCriteria? criteria = null, params MLoopEvaluation[] evaluations)
    {
        var mloop = new EvaluatingMLoop { Production = "exp-2" };
        foreach (var e in evaluations)
            mloop.Evaluations[e.ExperimentId] = e;
        return (new EvidencePromotionGate(mloop, criteria ?? new EvidenceCriteria { EvaluationDataPath = "eval.csv", Weight = "w" }), mloop);
    }

    [Fact]
    public async Task Evidence_PromotesACandidateShownNoWorse_OnTheHeldOutLabels()
    {
        var (gate, mloop) = Evidence(null, Scored("exp-4", -0.04, 0.0));

        var verdict = await gate.JudgeAsync(Candidate);

        Assert.Equal(PromotionDecision.Promote, verdict.Decision);
        Assert.True(verdict.ProvenNoWorse);
        Assert.Equal(JudgeKind.Statistics, verdict.DecidedBy);
        Assert.Contains("shown no worse", verdict.Reason);
        var asked = Assert.Single(mloop.Asked);
        Assert.Equal(new MLoopEvaluationRequest("eval.csv", "exp-4", "weld", "w", "production"), asked);
    }

    [Fact]
    public async Task Evidence_RefusesWhenTheIntervalReachesPastTheMargin_OrRestsOnTooFewRows()
    {
        var (worse, _) = Evidence(null, Scored("exp-4", -0.02, 0.03));
        var refused = await worse.JudgeAsync(Candidate);
        Assert.Equal(PromotionDecision.Refuse, refused.Decision);
        Assert.False(refused.ProvenNoWorse);
        Assert.Contains("not shown no worse", refused.Reason);

        // Within a registered margin of 0.05, the same interval is no worse.
        var (margin, _) = Evidence(new EvidenceCriteria { EvaluationDataPath = "eval.csv", Margin = 0.05 }, Scored("exp-4", -0.02, 0.03));
        Assert.Equal(PromotionDecision.Promote, (await margin.JudgeAsync(Candidate)).Decision);

        var (thin, _) = Evidence(null, Scored("exp-4", -0.04, 0.0, rows: 12));
        Assert.Contains("12 held-out rows", (await thin.JudgeAsync(Candidate)).Reason);
    }

    [Fact]
    public async Task Evidence_RefusesACandidateThatCatchesFewerMustCatchValues_ThoughNoWorseOverall()
    {
        var criteria = new EvidenceCriteria { EvaluationDataPath = "eval.csv", MustCatch = ["danger"], MustCatchRecallMargin = 0.02 };
        var (gate, mloop) = Evidence(criteria,
            Scored("exp-4", -0.05, -0.01, counts: [[98, 2], [12, 88]]),    // recall of danger 0.88
            Scored("exp-2", 0, 0, counts: [[90, 10], [5, 95]]));            // production 0.95

        var verdict = await gate.JudgeAsync(Candidate);

        Assert.Equal(PromotionDecision.Refuse, verdict.Decision);
        Assert.Contains("must-catch recall 0.88 under production's 0.95", verdict.Reason);
        Assert.Equal(2, mloop.Asked.Count);
        Assert.Null(mloop.Asked[1].Against);                                // production alone, on the same rows
        Assert.Equal("exp-2", mloop.Asked[1].ExperimentId);
    }

    [Fact]
    public async Task Evidence_PromotesTheFirstModel_AndRefusesWhatCannotBePaired()
    {
        var (first, mloop) = Evidence(null, Scored("exp-4", 0, 0));
        mloop.Production = null;
        var verdict = await first.JudgeAsync(Candidate);
        Assert.Equal(PromotionDecision.Promote, verdict.Decision);
        Assert.False(verdict.ProvenNoWorse);                               // nothing to be no worse than
        Assert.Empty(mloop.Asked);

        var (unpaired, _) = Evidence(null, Scored("exp-4", 0, 0) with { Paired = null, NotPaired = "ranking has no per-row measure" });
        Assert.Contains("per-row measure", (await unpaired.JudgeAsync(Candidate)).Reason);
    }

    [Fact]
    public async Task Review_WaitsForTheDecision_ThenFollowsIt()
    {
        var reviews = new InMemoryReviewStore();
        var gate = new ReviewPromotionGate(reviews);

        Assert.Equal(PromotionDecision.Wait, (await gate.JudgeAsync(Candidate)).Decision);
        Assert.Equal(PromotionDecision.Wait, (await gate.JudgeAsync(Candidate)).Decision);   // asked again: one review
        Assert.Single(await reviews.ListPendingAsync());

        await reviews.DecideAsync("rev-1", new ReviewDecision("Approve", "looks right"));
        var verdict = await gate.JudgeAsync(Candidate);
        Assert.Equal((PromotionDecision.Promote, "looks right", JudgeKind.Human), (verdict.Decision, verdict.Reason, verdict.DecidedBy));
    }

    private sealed class EvaluatingMLoop : IMLoopClient
    {
        public string? Production { get; set; }
        public Dictionary<string, MLoopEvaluation> Evaluations { get; } = new();
        public List<MLoopEvaluationRequest> Asked { get; } = new();

        public Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken ct = default)
            => Task.FromResult(Production is null ? null : new MLoopModelInfo(model ?? "default", "binary"));

        public Task<MLoopEvaluation> EvaluateAsync(MLoopEvaluationRequest request, CancellationToken ct = default)
        {
            Asked.Add(request);
            var evaluation = Evaluations[request.ExperimentId!];
            return Task.FromResult(request.Against is null ? evaluation with { Paired = null } : evaluation with { Against = Production });
        }

        public Task<MLoopRollback> RollbackAsync(string model, string? to = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ITracedPrediction<MLoopPredictionResult>>> PredictImagesAsync(MLoopImagePredictionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopEmbedding> EmbedAsync(MLoopEmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopEmbedding> EmbedTextsAsync(MLoopTextEmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ITracedPrediction<MLoopForecastResult>> ForecastAsync(MLoopForecastRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PromoteAsync(string model, string experimentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopDrift> GetDriftAsync(MLoopDriftRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordFeedbackAsync(MLoopFeedback feedback, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopSample> CreateSampleAsync(MLoopSampleRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
