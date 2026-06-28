using System.Collections.Generic;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Pins the Phase 0 contract shapes to the consumer surfaces they generalize
/// (back-derivation §3.5). These are not behaviour tests — they assert the declared
/// contract is consumable and carries the fields the two consumers independently built.
/// </summary>
public class ContractShapeTests
{
    [Fact]
    public void ReasoningLayer_LaddersCheapToCostly()
    {
        Assert.True((int)ReasoningLayer.Theory < (int)ReasoningLayer.Statistics);
        Assert.True((int)ReasoningLayer.Statistics < (int)ReasoningLayer.AutoMl);
        Assert.True((int)ReasoningLayer.AutoMl < (int)ReasoningLayer.Frontier);
    }

    [Fact]
    public void Provenance_CarriesTraceabilityAndReviewSignal()
    {
        // Mirrors U-Vision (agreement/requires_review) + SMI.AIMS (confidence/category).
        var p = new PredictionProvenance
        {
            SourceLayer = ReasoningLayer.AutoMl,
            Confidence = 0.42,
            Rationale = "ml_label disagreed with vlm_verdict",
            Agreement = false,
            RequiresReview = true,
            Annotations = new Dictionary<string, string> { ["category"] = "Security" },
        };

        Assert.Equal(ReasoningLayer.AutoMl, p.SourceLayer);
        Assert.Equal(0.42, p.Confidence);
        Assert.False(p.Agreement);
        Assert.True(p.RequiresReview);
        Assert.Equal("Security", p.Annotations!["category"]);
    }

    [Fact]
    public void TracedPrediction_AlwaysCarriesProvenance()
    {
        // "출처 없는 예측 = 컴파일 불가": Provenance is a required ctor argument.
        var provenance = new PredictionProvenance { SourceLayer = ReasoningLayer.Frontier, Confidence = 0.9 };
        ITracedPrediction<string> pred = new TracedPrediction<string>("OK", provenance);

        Assert.Equal("OK", pred.Value);
        Assert.Same(provenance, pred.Provenance);
    }

    [Fact]
    public void MLoopContracts_ExposePredictTrainPromoteInfoVocabulary()
    {
        // ④ transport-neutral client vocabulary, grounded in MloopClassifier / MLoopMcpService.
        var req = new MLoopPredictionRequest(
            new Dictionary<string, object?> { ["temp"] = 36.5 }, Model: "default");
        var result = new MLoopPredictionResult(new Dictionary<string, object?> { ["label"] = "NG" });
        var train = new MLoopTrainRequest("data.csv", Label: "target", Task: "binary");
        var job = new MLoopJob("exp-001", "running");
        var info = new MLoopModelInfo("default", Task: "binary",
            Metrics: new Dictionary<string, double> { ["auc"] = 0.91 });

        Assert.Equal("default", req.Model);
        Assert.Equal("NG", result.Outputs["label"]);
        Assert.Equal("target", train.Label);
        Assert.Equal("exp-001", job.Id);
        Assert.Equal(0.91, info.Metrics!["auc"]);
    }

    [Fact]
    public async Task ReasoningRouter_ContractIsImplementable()
    {
        // Compile-time proof the router contract is consumable; a stub climbs to L3.
        IReasoningRouter<string, string> router = new StubRouter();
        var pred = await router.RouteAsync("anything");

        Assert.Equal("escalated", pred.Value);
        Assert.Equal(ReasoningLayer.Frontier, pred.Provenance.SourceLayer);
        Assert.True(pred.Provenance.RequiresReview);
    }

    private sealed class StubRouter : IReasoningRouter<string, string>
    {
        public Task<ITracedPrediction<string>> RouteAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            var provenance = new PredictionProvenance
            {
                SourceLayer = ReasoningLayer.Frontier,
                Confidence = 0.3,
                Agreement = false,
                RequiresReview = true,
            };
            return Task.FromResult<ITracedPrediction<string>>(
                new TracedPrediction<string>("escalated", provenance));
        }
    }
}
