using System;
using System.Linq;
using HoneAI;
using HoneAI.Learning;
using Xunit;

namespace HoneAI.Tests;

public class RetrainPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly RetrainPolicy Policy = new() { MinimumNewLabels = 200, MinimumInterval = TimeSpan.FromDays(1) };

    [Fact]
    public void EnoughNewLabels_StartARun_ButNeverTwoAtOnce_OrCloserThanTheInterval()
    {
        var enough = new RetrainSignals { LastTrainedAt = Now.AddDays(-3), NewLabels = 250 };
        Assert.True(Policy.Decide(enough, Now).Start);
        Assert.True(Policy.Decide(enough with { LastTrainedAt = null }, Now).Start);           // the first training

        Assert.False(Policy.Decide(enough with { RunInFlight = true }, Now).Start);
        var tooSoon = Policy.Decide(enough with { LastTrainedAt = Now.AddHours(-5) }, Now);
        Assert.False(tooSoon.Start);
        Assert.Contains("minimum interval", tooSoon.Reason);

        var few = Policy.Decide(enough with { NewLabels = 40 }, Now);
        Assert.False(few.Start);
        Assert.Contains("under 200", few.Reason);
    }

    [Fact]
    public void AStaleOrDemotedModel_RetrainsOnAnyNewLabel_ButNotOnNothing()
    {
        var some = new RetrainSignals { LastTrainedAt = Now.AddDays(-3), NewLabels = 12 };
        Assert.Contains("demoted", Policy.Decide(some with { Demoted = true }, Now).Reason);
        Assert.True(Policy.Decide(some with { Demoted = true }, Now).Start);
        Assert.True(Policy.Decide(some with { Stale = true }, Now).Start);
        Assert.False((Policy with { OnStaleness = false }).Decide(some with { Stale = true }, Now).Start);

        var nothing = Policy.Decide(some with { NewLabels = 0, Demoted = true }, Now);
        Assert.False(nothing.Start);
        Assert.Contains("nothing new", nothing.Reason);

        // The interval holds for signals too — a flapping drift signal does not retrain on every flap.
        Assert.False(Policy.Decide(some with { Stale = true, LastTrainedAt = Now.AddHours(-1) }, Now).Start);
    }


    [Fact]
    public void ARunTheGateWouldRefuseForWantOfHeldOutRows_IsNotStarted()
    {
        var gated = Policy with { MinimumEvaluationRows = 30 };
        var plenty = new RetrainSignals { LastTrainedAt = Now.AddDays(-3), NewLabels = 500, NewEvaluationRows = 12 };

        var refused = gated.Decide(plenty, Now);

        Assert.False(refused.Start);
        Assert.Contains("12 held-out rows", refused.Reason);
        Assert.Contains("30", refused.Reason);
        Assert.True(gated.Decide(plenty with { NewEvaluationRows = 30 }, Now).Start);
        // Stale or demoted models are held to it too: the gate refuses them as it refuses any.
        Assert.False(gated.Decide(plenty with { NewLabels = 12, Stale = true }, Now).Start);
    }

    [Fact]
    public void WithoutAHeldOutMeasure_OrWithoutTheThreshold_NothingChanges()
    {
        var signals = new RetrainSignals { LastTrainedAt = Now.AddDays(-3), NewLabels = 500 };

        Assert.True((Policy with { MinimumEvaluationRows = 30 }).Decide(signals, Now).Start);                      // not measured
        Assert.True(Policy.Decide(signals with { NewEvaluationRows = 0 }, Now).Start);                              // no threshold
        Assert.Equal(Policy.Decide(signals, Now), Policy.Decide(signals with { ConsecutiveRefusals = 3 }, Now));   // refusals are read only when asked
    }

    [Fact]
    public void AfterTheGateRefusedTheLastRun_AStaleModelCanWaitForTheFullAmount()
    {
        var careful = Policy with { ShortcutsAfterRefusal = false };
        var some = new RetrainSignals { LastTrainedAt = Now.AddDays(-3), NewLabels = 12, Stale = true, Demoted = true };

        Assert.True(careful.Decide(some, Now).Start);                                          // never refused: the shortcut stands
        var waiting = careful.Decide(some with { ConsecutiveRefusals = 2 }, Now);
        Assert.False(waiting.Start);
        Assert.Contains("refused the last 2 runs", waiting.Reason);
        Assert.True(careful.Decide(some with { ConsecutiveRefusals = 2, NewLabels = 250 }, Now).Start);   // the full amount still starts one
        Assert.True(Policy.Decide(some with { ConsecutiveRefusals = 2 }, Now).Start);          // default: as before
    }

    [Fact]
    public void TheEffectiveSample_CountsEvenlyWeightedLabelsByTheirNumber_AndSkewedOnesForLess()
    {
        static ConfirmedLabel<string> Label(int i, double probability) => new()
        {
            DecisionId = $"d{i}",
            Value = "ok",
            ConfirmedBy = "llm",
            Kind = JudgeKind.Llm,
            Probability = probability,
            Trust = 1,
            TrustBasis = LabelTrustBasis.Default,
            ConfirmedAt = Now,
        };

        Assert.Equal(100, RetrainSignals.EffectiveSample(Enumerable.Range(0, 100).Select(i => Label(i, 0.5))), 9);
        // One decision drawn at π = 0.001 stands for a thousand; next to 99 at π = 1 the hundred count for about 1.2.
        var skewed = RetrainSignals.EffectiveSample(Enumerable.Range(0, 99).Select(i => Label(i, 1)).Append(Label(99, 0.001)));
        Assert.InRange(skewed, 1, 1.5);
        Assert.Equal(0, RetrainSignals.EffectiveSample(new[] { Label(0, 1) with { Share = LabelShare.Evaluation, ShareProbability = 0.2 } }));
        Assert.Equal(0, RetrainSignals.EffectiveSample(Array.Empty<ConfirmedLabel<string>>()));
    }
}
