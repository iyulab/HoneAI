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
