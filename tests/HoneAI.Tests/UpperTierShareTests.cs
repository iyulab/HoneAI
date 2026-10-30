using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class UpperTierShareTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static ProvenanceRecord Decision(int day, int steps = 1, bool drawn = false, string slice = "a")
        => new("d-" + Guid.NewGuid().ToString("N"), new PredictionProvenance
        {
            Kind = JudgeKind.Ml,
            SliceKey = slice,
            Path = Enumerable.Range(0, steps).Select(i => new TierStep("t" + i, JudgeKind.Ml, i == steps - 1 ? StepOutcome.Settled : StepOutcome.Escalated)).ToList(),
            Sample = new SampleDraw(0.1, "floor", drawn),
        }, Origin.AddDays(day));

    [Fact]
    public void AFirstTierThatSettlesEverythingUndrawn_CostsNoUpperCalls()
    {
        var curve = UpperTierShare.Curve([Decision(1), Decision(2), Decision(3)], Week, Origin);

        var only = Assert.Single(curve);
        Assert.Equal(3, only.Decisions);
        Assert.Equal(0, only.Share);
        Assert.Equal(0, only.Calls);
    }

    [Fact]
    public void AnEscalationAndADrawnRecheck_AreCountedApart_AndAClimbThroughSeveralTiersCostsSeveralCalls()
    {
        var records = new[]
        {
            Decision(1),                          // settled below, not drawn
            Decision(1, drawn: true),             // settled below, rechecked
            Decision(2, steps: 2),                // climbed one tier
            Decision(2, steps: 3, drawn: true),   // climbed two tiers, and was drawn
        };

        var week = Assert.Single(UpperTierShare.Curve(records, Week, Origin));

        Assert.Equal(4, week.Decisions);
        Assert.Equal(2, week.Escalated);
        Assert.Equal(1, week.Rechecked);                 // the climbed-and-drawn one is escalated, not counted twice
        Assert.Equal(0.75, week.Share);
        Assert.Equal(1 + 0 + 1 + 3, week.Calls);         // 1 recheck, 0, 1 extra step, 2 extra steps + 1 recheck
        Assert.Equal(5 / 4.0, week.CallsPerDecision);
    }

    [Fact]
    public void TheCurveFalls_AsTheFirstTierEarnsTheWork_AndEmptyWindowsAreLeftOut()
    {
        var records = new List<ProvenanceRecord>();
        records.AddRange(Enumerable.Range(0, 10).Select(_ => Decision(1, steps: 2)));        // week 0: everything climbs
        records.AddRange(Enumerable.Range(0, 10).Select(i => Decision(15, steps: i < 3 ? 2 : 1)));   // week 2: 3 of 10 (week 1 is empty)

        var curve = UpperTierShare.Curve(records, Week, Origin);

        Assert.Equal(2, curve.Count);
        Assert.Equal(Origin, curve[0].Start);
        Assert.Equal(1.0, curve[0].Share);
        Assert.Equal(Origin.AddDays(14), curve[1].Start);
        Assert.Equal(Origin.AddDays(21), curve[1].End);
        Assert.Equal(0.3, curve[1].Share, 12);
    }

    [Fact]
    public void ASliceCanBeAsked_AndRecordsBeforeTheOriginOrWithoutAPathAreNotDecisions()
    {
        var records = new[]
        {
            Decision(1, steps: 2, slice: "a"),
            Decision(1, slice: "b"),
            new ProvenanceRecord("early", new PredictionProvenance { Kind = JudgeKind.Ml, SliceKey = "a", Path = [new TierStep("t0", JudgeKind.Ml, StepOutcome.Settled)] }, Origin.AddDays(-1)),
            new ProvenanceRecord("no-path", new PredictionProvenance { Kind = JudgeKind.Human, SliceKey = "a" }, Origin.AddDays(1)),
        };

        var a = Assert.Single(UpperTierShare.Curve(records, Week, Origin, sliceKey: "a"));
        var all = Assert.Single(UpperTierShare.Curve(records, Week, Origin));

        Assert.Equal(1, a.Decisions);
        Assert.Equal(1.0, a.Share);
        Assert.Equal(2, all.Decisions);
    }

    [Fact]
    public void ANonPositiveWindow_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => UpperTierShare.Curve([], TimeSpan.Zero, Origin));
}
