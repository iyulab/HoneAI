using System.Collections.Generic;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Pins the human-review and lifecycle contract record/enum shapes: a review decision
/// and a train → evaluate → review → promote step machine, the shapes applications hand-write
/// today. Review <em>behaviour</em> is covered by <see cref="ReviewStoreTests"/> against the reference implementations.
/// </summary>
public class HitlAndLifecycleContractTests
{
    [Fact]
    public void LifecycleStage_OrdersExportToPromote()
    {
        // The stages run export, init, info, train, evaluate, review, promote — in that order.
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

        // A decision may come without a comment.
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
