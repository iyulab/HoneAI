using System.Collections.Generic;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Pins the Phase 0 HITL (③) and lifecycle (⑤) contract record/enum shapes to the
/// consumer surfaces they generalize — SMI.AIMS's <c>ReviewDecision</c> and
/// <c>MLoopPipelineService</c> step machine (back-derivation §3.5). Gate <em>behaviour</em>
/// is covered by <see cref="InMemoryHitlGateTests"/> against the production implementation.
/// </summary>
public class HitlAndLifecycleContractTests
{
    [Fact]
    public void LifecycleStage_OrdersExportToPromote()
    {
        // One-to-one with SMI StepType export|init|info|train|evaluate|review|promote.
        Assert.Equal(0, (int)LifecycleStage.Export);
        Assert.Equal(5, (int)LifecycleStage.Review);
        Assert.Equal(6, (int)LifecycleStage.Promote);
        Assert.True((int)LifecycleStage.Train < (int)LifecycleStage.Evaluate);
        Assert.True((int)LifecycleStage.Evaluate < (int)LifecycleStage.Review);
    }

    [Fact]
    public void ReviewDecision_CarriesVerdictAndComment()
    {
        var d = new ReviewDecision("approve", "looks good");
        Assert.Equal("approve", d.Verdict);
        Assert.Equal("looks good", d.Comment);

        // Comment is optional (SMI SubmitReview comment is nullable).
        Assert.Null(new ReviewDecision("reject").Comment);
    }

    [Fact]
    public void LifecycleRun_AccumulatesStepTrail()
    {
        var run = new LifecycleRun("run-1", "running", new List<LifecycleStep>
        {
            new(LifecycleStage.Export, "completed"),
            new(LifecycleStage.Train, "running", "exp-001"),
        });

        Assert.Equal("run-1", run.RunId);
        Assert.Equal(2, run.Steps.Count);
        Assert.Equal(LifecycleStage.Train, run.Steps[1].Stage);
        Assert.Equal("exp-001", run.Steps[1].Detail);
    }
}
