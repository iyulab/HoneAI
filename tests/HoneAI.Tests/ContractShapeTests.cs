using System.Collections.Generic;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Pins the Phase 0 contract shapes to the consumer surfaces they generalize.
/// These are not behaviour tests — they assert the declared
/// contract is consumable and carries the fields the two consumers independently built.
/// </summary>
public class ContractShapeTests
{
    [Fact]
    public void Tier_CarriesTheLadderOrder_NotTheKind()
    {
        // Two judges of one kind at different sites are two tiers; the cost order is the tier's.
        var edge = new Tier { Id = "edge-ml", Kind = JudgeKind.Ml, Version = "m-7", CostRank = 1, Site = "edge" };
        var central = new Tier { Id = "central-ml", Kind = JudgeKind.Ml, Version = "m-9", CostRank = 2, Site = "central" };

        Assert.Equal(edge.Kind, central.Kind);
        Assert.True(edge.CostRank < central.CostRank);
        Assert.False(edge.AcceptsHints);
    }

    [Fact]
    public void Provenance_CarriesThePathTheDecisionTook()
    {
        // The two flags the 2-tier router set (agreement, requires review) are now what each tier did.
        var p = new PredictionProvenance
        {
            Kind = JudgeKind.Llm,
            TierId = "teacher",
            TierVersion = "prompt-12",
            Confidence = null,
            SliceKey = "template:42",
            TrustState = TrustState.Active,
            SnapshotVersion = "snap-3",
            Path = new[]
            {
                new TierStep("memory", JudgeKind.System1, StepOutcome.Abstained, "no settled answer for the key"),
                new TierStep("edge-ml", JudgeKind.Ml, StepOutcome.Escalated, "must-catch value"),
                new TierStep("teacher", JudgeKind.Llm, StepOutcome.Settled),
            },
            Annotations = new Dictionary<string, string> { ["category"] = "Security" },
        };

        Assert.Null(p.Confidence);
        Assert.Equal(StepOutcome.Settled, p.Path![^1].Outcome);
        Assert.Equal(JudgeKind.Ml, p.Path[1].Kind);
        Assert.Null(p.Sample);
        Assert.False(p.Unverified);
    }

    [Fact]
    public void JudgeVerdict_IsAnAnswerOrAnAbstention()
    {
        var answer = JudgeVerdict<string>.Answer("NG", 0.81, "edge model");
        var abstain = JudgeVerdict<string>.Abstain("timeout");

        Assert.True(answer.Answered);
        Assert.Equal("NG", answer.Value);
        Assert.False(abstain.Answered);
        Assert.Null(abstain.Value);
        Assert.Equal("timeout", abstain.Rationale);
    }

    [Fact]
    public void SettlementPolicy_NeverSettlesAMustCatchValue()
    {
        var tier = new Tier { Id = "edge-ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
        // Even a policy that lists a value as settleable cannot settle it if it must be caught.
        var policy = SettlementPolicy.Categorical(new[] { "OK", "NG" }, new[] { "NG" });

        Assert.True(policy.MaySettle(tier, "OK"));
        Assert.False(policy.MaySettle(tier, "NG"));
        Assert.True(policy.MustCatch("NG"));
        Assert.True(policy.Agrees("OK", "OK"));
    }

    [Theory]
    [InlineData(10.0, 10.4, true)]
    [InlineData(10.0, 10.6, false)]
    public void SettlementPolicy_Tolerance_AgreesWithinTheTolerance(double a, double b, bool agrees)
        => Assert.Equal(agrees, SettlementPolicy.Tolerance(0.5).Agrees(a, b));

    [Theory]
    [InlineData(9.9, 9.0, true)]    // both below the limit
    [InlineData(9.9, 10.1, false)]  // the limit falls between them
    public void SettlementPolicy_SameSideOf_AgreesOnTheVerdictTheValuesImply(double a, double b, bool agrees)
        => Assert.Equal(agrees, SettlementPolicy.SameSideOf(10.0).Agrees(a, b));

    [Fact]
    public void TracedPrediction_AlwaysCarriesProvenance()
    {
        // "출처 없는 예측 = 컴파일 불가": Provenance is a required ctor argument.
        var provenance = new PredictionProvenance { Kind = JudgeKind.Llm, Confidence = 0.9 };
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
        // Compile-time proof the router contract is consumable; a stub settles at the top tier.
        IReasoningRouter<string, string> router = new StubRouter();
        var pred = await router.RouteAsync("anything");

        Assert.Equal("escalated", pred.Value);
        Assert.Equal(JudgeKind.Human, pred.Provenance.Kind);
    }

    private sealed class StubRouter : IReasoningRouter<string, string>
    {
        public Task<ITracedPrediction<string>> RouteAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            var provenance = new PredictionProvenance
            {
                Kind = JudgeKind.Human,
                Path = new[] { new TierStep("edge-ml", JudgeKind.Ml, StepOutcome.Abstained), new TierStep("reviewer", JudgeKind.Human, StepOutcome.Settled) },
            };
            return Task.FromResult<ITracedPrediction<string>>(
                new TracedPrediction<string>("escalated", provenance));
        }
    }
}
