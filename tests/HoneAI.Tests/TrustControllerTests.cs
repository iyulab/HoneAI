using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class TrustControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(14);

    private static readonly ControllerPolicy Policy = new(
        new LifecyclePolicy
        {
            Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
            Margin = 0.01,
            MinimumEffectiveSample = 300,
            MinimumDwell = TimeSpan.FromDays(7),
            StableWindows = 2,
        },
        new IssuePolicy { SoftLife = TimeSpan.FromDays(1), HardLife = TimeSpan.FromDays(3), ActiveRate = 0.3 },
        new Dictionary<string, double> { ["tenant-a"] = 2_000 });

    private static LedgerCell Good(double? missUpper = 0.01) => new()
    {
        TierId = "ml",
        SliceKey = "s",
        WindowStart = Start,
        WindowEnd = Now,
        Rechecks = 500,
        UpperFailures = 0,
        EffectiveSampleSize = 500,
        Agreement = 0.98,
        AgreementLower = 0.97,
        AgreementUpper = 0.99,
        MissesEstimated = 0,
        Caught = 400,
        MissRate = 0,
        MissRateUpper = missUpper,
        MissRateLower = 0,
        Confidence = 0.95,
    };

    private static SliceWindow Slice(string key, TrustState state, LedgerCell window, bool drifted = false, double decisions = 10_000, double caught = 3_000)
        => new("ml", key, "tenant-a", new SliceStanding(state, Start, "v1"), window, true, new LiveSignals("v1", drifted), new SliceTraffic(decisions, caught));

    [Fact]
    public void AWindowClose_JudgesEverySlice_SharesThePool_AndIssuesTheSnapshot()
    {
        var step = TrustController.Close("s-12", Now,
        [
            Slice("ready", TrustState.Shadow, Good()),
            Slice("active", TrustState.Active, Good() with { EffectiveSampleSize = 10 }),
            Slice("stable", TrustState.Stable, Good()),
            Slice("drifted", TrustState.Stable, Good(), drifted: true),
        ], Policy);

        var outcome = step.Slices.ToDictionary(s => s.SliceKey);
        var entry = step.Snapshot.Entries.ToDictionary(e => e.SliceKey);

        // A release waits for approval: the snapshot keeps the shadow standing.
        Assert.Equal(["ready"], step.AwaitingApproval.Select(s => s.SliceKey));
        Assert.Equal(TrustState.Shadow, entry["ready"].State);
        Assert.Equal(TrustState.Active, outcome["ready"].Step.Standing.State);

        // Tightening is committed at once.
        Assert.Equal(TrustState.Active, entry["drifted"].State);
        Assert.Equal(0.3, entry["drifted"].SampleRates!["uniform"]);

        // Two active slices at 0.3 × 10,000 already overrun the 2,000 pool: the stable one keeps only its floor,
        // and the snapshot says the budget set it.
        Assert.True(step.Pools["tenant-a"].OverBudget);
        Assert.Equal(TrustState.Stable, entry["stable"].State);
        Assert.Equal(0.01, entry["stable"].SampleRates!["uniform"], 9);
        Assert.Equal(RateTerm.Budget, entry["stable"].RateSetBy);
        Assert.False(outcome["stable"].Grant!.Funded);
    }

    private static readonly SliceFreeze Hold = new("operator-1", Now.AddDays(-1), "manual labels under review");

    [Fact]
    public void AFrozenSlice_IsNotReleased_AndIsNotWaitingForAnApproval_ButTheWindowIsStillJudged()
    {
        foreach (var policy in new[] { Policy, Policy with { Lifecycle = Policy.Lifecycle with { AutoRelease = true } } })
        {
            var step = TrustController.Close("s-20", Now, [Slice("ready", TrustState.Shadow, Good()) with { Freeze = Hold }], policy);

            var outcome = Assert.Single(step.Slices);
            Assert.Equal(TrustState.Shadow, step.Snapshot.Entries[0].State);
            Assert.Equal(TrustState.Shadow, outcome.Committed.State);
            Assert.False(outcome.Step.Moved);
            Assert.True(outcome.Held);
            Assert.Empty(step.AwaitingApproval);
            Assert.Same(Hold, Assert.Single(step.Frozen).Freeze);
            Assert.Contains("frozen by operator-1", outcome.Step.Reason);
            Assert.Contains("window met every release condition", outcome.Step.Reason);   // what it would have done
        }

        // The same slice without the hold is released (or waits for approval): the hold is what stopped it.
        var free = TrustController.Close("s-21", Now, [Slice("ready", TrustState.Shadow, Good())], Policy);
        Assert.Equal(["ready"], free.AwaitingApproval.Select(s => s.SliceKey));
    }

    [Fact]
    public void AFrozenActiveSlice_DoesNotCountWindowsTowardStable()
    {
        var step = TrustController.Close("s-22", Now, [Slice("a", TrustState.Active, Good()) with { Freeze = Hold }], Policy);

        Assert.Equal(0, step.Slices[0].Committed.PassingWindows);
        Assert.True(step.Slices[0].Held);
        var free = TrustController.Close("s-23", Now, [Slice("a", TrustState.Active, Good())], Policy);
        Assert.Equal(1, free.Slices[0].Committed.PassingWindows);
    }

    [Fact]
    public void AFrozenSlice_IsStillTightened_BecauseTheHoldStopsReleasesNotTheSafetyNet()
    {
        var step = TrustController.Close("s-24", Now, [
            Slice("drifted", TrustState.Stable, Good(), drifted: true) with { Freeze = Hold },
            Slice("failing", TrustState.Active, Good() with { AgreementUpper = 0.5, AgreementLower = 0.4, Agreement = 0.45 }) with { Freeze = Hold },
        ], Policy);

        var outcome = step.Slices.ToDictionary(s => s.SliceKey);
        Assert.Equal(TrustState.Active, outcome["drifted"].Committed.State);
        Assert.Equal(TrustState.Shadow, outcome["failing"].Committed.State);
        Assert.All(outcome.Values, o => Assert.False(o.Held));
        Assert.Equal(2, step.Frozen.Count());                                    // still frozen; nothing was held this window
    }

    [Fact]
    public void AFrozenStableSlice_IsRecheckedNoLessThanItsHold_AndThePoolCountsTheExtra()
    {
        var free = TrustController.Close("s-25", Now, [Slice("stable", TrustState.Stable, Good())], Policy);
        var frozen = TrustController.Close("s-26", Now, [Slice("stable", TrustState.Stable, Good()) with { Freeze = Hold with { MinimumRate = 0.1 } }], Policy);

        var floor = frozen.Snapshot.Entries[0];
        Assert.True(free.Snapshot.Entries[0].SampleRates!["uniform"] < 0.1);
        Assert.Equal(0.1, floor.SampleRates!["uniform"], 12);
        Assert.Equal(SliceFreeze.RateTerm, floor.RateSetBy);
        Assert.True(frozen.Slices[0].Held);
        Assert.Equal(free.Pools["tenant-a"].Spent + (0.1 - free.Snapshot.Entries[0].SampleRates!["uniform"]) * 10_000, frozen.Pools["tenant-a"].Spent, 6);

        // A grant already above the hold is left as it is.
        var above = TrustController.Close("s-27", Now, [Slice("stable", TrustState.Stable, Good()) with { Freeze = Hold with { MinimumRate = 0.0001 } }], Policy);
        Assert.Equal(free.Snapshot.Entries[0].SampleRates!["uniform"], above.Snapshot.Entries[0].SampleRates!["uniform"], 12);
        Assert.False(above.Slices[0].Held);
    }

    [Fact]
    public void AFreeze_NeedsAWhoAWhyAndARateInRange()
    {
        Assert.Throws<ArgumentException>(() => TrustController.Close("s", Now, [Slice("a", TrustState.Active, Good()) with { Freeze = Hold with { By = " " } }], Policy));
        Assert.Throws<ArgumentException>(() => TrustController.Close("s", Now, [Slice("a", TrustState.Active, Good()) with { Freeze = Hold with { Reason = "" } }], Policy));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrustController.Close("s", Now, [Slice("a", TrustState.Stable, Good()) with { Freeze = Hold with { MinimumRate = 0 } }], Policy));
    }

    [Fact]
    public void AStableTierWithinBudget_IsGrantedWhatItsPromiseNeeds()
    {
        var step = TrustController.Close("s-13", Now, [Slice("stable", TrustState.Stable, Good())], Policy);

        var outcome = step.Slices.Single();
        Assert.True(outcome.Grant!.Funded);
        Assert.Equal(outcome.Need.Rate, step.Snapshot.Entries[0].SampleRates!["uniform"], 9);
        Assert.False(step.Pools["tenant-a"].OverBudget);
    }

    [Fact]
    public void AnUnmeasuredTier_IsFundedBeforeAMeasuredOne()
    {
        // Each slice needs 587 rechecks a window (the agreement tests, over 10,000 decisions): the pool funds one in full after both floors.
        var policy = Policy with { PoolBudgets = new Dictionary<string, double> { ["tenant-a"] = 700 } };

        var step = TrustController.Close("s-14", Now,
        [
            Slice("measured", TrustState.Stable, Good(missUpper: 0.015)),
            Slice("unmeasured", TrustState.Stable, Good(missUpper: null) with { Caught = 0 }, caught: 3_000),
        ], policy);

        var grant = step.Slices.ToDictionary(s => s.SliceKey, s => s.Grant!);
        Assert.True(grant["unmeasured"].Funded);
        Assert.False(grant["measured"].Funded);
    }

    [Fact]
    public void Exposure_IsTheMissBoundOverTheCaughtDecisions()
    {
        Assert.Null(TrustController.Exposure(Good(missUpper: null)));
        Assert.Equal(0.02 * 400 / 0.98, TrustController.Exposure(Good(missUpper: 0.02))!.Value, 9);
    }

    [Fact]
    public void ASliceInAPoolWithoutABudget_IsRefused()
        => Assert.Throws<ArgumentException>(() => TrustController.Close("s", Now, [Slice("x", TrustState.Shadow, Good()) with { Pool = "nowhere" }], Policy));
}

public class ConfidenceAbstainTests
{
    private static LedgerCell Window(double? auc, double ess = 500, double agreement = 0.9) => new()
    {
        TierId = "ml",
        SliceKey = "s",
        WindowStart = DateTimeOffset.UnixEpoch,
        WindowEnd = DateTimeOffset.UnixEpoch.AddDays(7),
        Rechecks = (int)ess,
        UpperFailures = 0,
        EffectiveSampleSize = ess,
        Agreement = agreement,
        MissesEstimated = 0,
        Caught = 0,
        Confidence = 0.95,
        ConfidenceRankAuc = auc,
    };

    [Fact]
    public void ARankingThatWorks_EarnsTheAbstainQuantile()
        => Assert.Equal(0.05, ConfidenceAbstain.Quantile(Window(0.8), 0.05));

    [Theory]
    [InlineData(0.52)]      // above chance, but not shown to be on 500 rechecks with 50 disagreements
    [InlineData(0.3)]       // confidence runs against correctness
    public void ARankingNotShownAboveChance_DoesNotAbstain(double auc)
        => Assert.Null(ConfidenceAbstain.Quantile(Window(auc), 0.05));

    [Fact]
    public void TooFewDisagreements_OrNoAuc_OrNoCalibration_DoNotAbstain()
    {
        Assert.Null(ConfidenceAbstain.Quantile(Window(0.9, ess: 5), 0.05));
        Assert.Null(ConfidenceAbstain.Quantile(Window(null), 0.05));
        Assert.Null(ConfidenceAbstain.Quantile(Window(0.8), 0.05, calibrated: false));
        Assert.Null(ConfidenceAbstain.Quantile(Window(0.8), null));
    }

    [Fact]
    public void TheLowerBound_TightensWithMoreRechecks()
        => Assert.True(ConfidenceAbstain.RankAucLower(Window(0.6, ess: 5_000)) > ConfidenceAbstain.RankAucLower(Window(0.6, ess: 500)));
}
