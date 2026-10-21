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
        TierId = "ml", SliceKey = "s", WindowStart = Start, WindowEnd = Now,
        Rechecks = 500, UpperFailures = 0, EffectiveSampleSize = 500,
        Agreement = 0.98, AgreementLower = 0.97, AgreementUpper = 0.99,
        MissesEstimated = 0, Caught = 400, MissRate = 0, MissRateUpper = missUpper, MissRateLower = 0, Confidence = 0.95,
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
        var policy = Policy with { PoolBudgets = new Dictionary<string, double> { ["tenant-a"] = 600 } };

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
