using System;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class TrustLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Start.AddDays(14);
    private static readonly LiveSignals V1 = new("v1");

    private static readonly LifecyclePolicy Policy = new()
    {
        Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
        Margin = 0.01,
        MinimumEffectiveSample = 300,
        MinimumDwell = TimeSpan.FromDays(7),
        StableWindows = 2,
    };

    private static LedgerCell Cell(double agreeLower = 0.97, double agreeUpper = 0.99, double? missUpper = 0.01,
        double? missLower = 0, double missesEstimated = 0, double ess = 500, int rechecks = 500, int failures = 0)
        => new()
        {
            TierId = "ml",
            SliceKey = "s",
            WindowStart = Start,
            WindowEnd = Later,
            Rechecks = rechecks,
            UpperFailures = failures,
            EffectiveSampleSize = ess,
            Agreement = (agreeLower + agreeUpper) / 2,
            AgreementLower = agreeLower,
            AgreementUpper = agreeUpper,
            MissesEstimated = missesEstimated,
            Caught = 400,
            MissRate = 0,
            MissRateUpper = missUpper,
            MissRateLower = missLower,
            Confidence = 0.95,
        };

    [Fact]
    public void AWindowMeetingEveryCondition_ReleasesShadowToActive_PendingApproval()
    {
        var step = TrustLifecycle.Release(SliceStanding.New("v1", Start), Cell(), teacherQualified: true, V1, Policy, Later);

        Assert.Equal(TrustState.Active, step.To);
        Assert.True(step.AwaitsApproval);                     // no automatic release before the shadow measurement
        Assert.Equal(Later, step.Standing.Since);
    }

    [Fact]
    public void AutoRelease_TakesEffectWithoutApproval()
    {
        var step = TrustLifecycle.Release(SliceStanding.New("v1", Start), Cell(), true, V1, Policy with { AutoRelease = true }, Later);

        Assert.False(step.AwaitsApproval);
    }

    [Theory]
    [InlineData("dwell")]
    [InlineData("sample")]
    [InlineData("agreement")]
    [InlineData("miss")]
    [InlineData("teacher")]
    [InlineData("upper")]
    public void EachReleaseCondition_HoldsTheTierWhereItIs(string missing)
    {
        var standing = SliceStanding.New("v1", Start);
        var (cell, teacher, now) = missing switch
        {
            "dwell" => (Cell(), true, Start.AddDays(3)),
            "sample" => (Cell(ess: 100), true, Later),
            "agreement" => (Cell(agreeLower: 0.955), true, Later),   // above p0, under p0 + ε
            "miss" => (Cell(missUpper: 0.05), true, Later),
            "teacher" => (Cell(), false, Later),
            _ => (Cell(failures: 100), true, Later),
        };

        var step = TrustLifecycle.Release(standing, cell, teacher, V1, Policy, now);

        Assert.False(step.Moved);
        Assert.Equal(TrustState.Shadow, step.To);
    }

    [Fact]
    public void NoCaughtDecision_CannotBoundTheMissRate_AndDoesNotRelease()
    {
        var step = TrustLifecycle.Release(SliceStanding.New("v1", Start), Cell(missUpper: null), true, V1, Policy, Later);

        Assert.False(step.Moved);
        Assert.Contains("bound the miss rate", step.Reason);
    }

    [Fact]
    public void ActiveBecomesStable_OnlyAfterKWindowsInARow()
    {
        var active = new SliceStanding(TrustState.Active, Start, "v1");

        var first = TrustLifecycle.Release(active, Cell(), true, V1, Policy, Later);
        Assert.False(first.Moved);
        Assert.Equal(1, first.Standing.PassingWindows);

        var failing = TrustLifecycle.Release(first.Standing, Cell(ess: 10), true, V1, Policy, Later.AddDays(7));
        Assert.Equal(0, failing.Standing.PassingWindows);   // the run starts over

        var again = TrustLifecycle.Release(failing.Standing, Cell(), true, V1, Policy, Later.AddDays(14));
        var second = TrustLifecycle.Release(again.Standing, Cell(), true, V1, Policy, Later.AddDays(21));
        Assert.Equal(TrustState.Stable, second.To);
    }

    [Fact]
    public void ASeenMiss_SendsStableBackToActive_WithoutApproval()
    {
        var stable = new SliceStanding(TrustState.Stable, Start, "v1");

        var step = TrustLifecycle.Tighten(stable, Cell(missesEstimated: 25), V1, Policy, Later);

        Assert.Equal(TrustState.Active, step.To);
        Assert.False(step.AwaitsApproval);
    }

    [Fact]
    public void AMissRateShownAboveTheLimit_DemotesToShadow()
    {
        var step = TrustLifecycle.Tighten(new SliceStanding(TrustState.Active, Start, "v1"), Cell(missLower: 0.03, missesEstimated: 50), V1, Policy, Later);

        Assert.Equal(TrustState.Shadow, step.To);
    }

    [Fact]
    public void Hysteresis_AgreementUnderTheTargetTightens_UnderTheMarginDemotes()
    {
        var stable = new SliceStanding(TrustState.Stable, Start, "v1");

        Assert.Equal(TrustState.Active, TrustLifecycle.Tighten(stable, Cell(agreeLower: 0.92, agreeUpper: 0.945), V1, Policy, Later).To);
        Assert.Equal(TrustState.Shadow, TrustLifecycle.Tighten(stable, Cell(agreeLower: 0.90, agreeUpper: 0.935), V1, Policy, Later).To);
        // An active tier between p0 − ε and p0 is neither released nor demoted.
        Assert.False(TrustLifecycle.Tighten(new SliceStanding(TrustState.Active, Start, "v1"), Cell(agreeLower: 0.92, agreeUpper: 0.945), V1, Policy, Later).Moved);
    }

    [Fact]
    public void Drift_SendsStableBackToActive()
        => Assert.Equal(TrustState.Active,
            TrustLifecycle.Tighten(new SliceStanding(TrustState.Stable, Start, "v1"), Cell(), V1 with { Drifted = true }, Policy, Later).To);

    [Fact]
    public void ANewTierVersion_StartsOverInShadow()
    {
        var step = TrustLifecycle.Release(new SliceStanding(TrustState.Stable, Start, "v1"), Cell(), true, new LiveSignals("v2"), Policy, Later);

        Assert.Equal(TrustState.Shadow, step.To);
        Assert.Equal("v2", step.Standing.TierVersion);
    }
}

public class MissRateLowerBoundTests
{
    [Fact]
    public void TheLowerBound_IsZeroWithoutASeenMiss_AndBelowTheEstimateWithOne()
    {
        Assert.Equal(0, TrustLedger.BinomialCountLowerBound(0, 0.1, 0.05));
        var low = TrustLedger.BinomialCountLowerBound(3, 0.1, 0.05);
        Assert.InRange(low, 3, 30);                              // three seen at π = 0.1 estimate 30
        Assert.True(1 - TrustLedger.BinomialCdf(2, (long)low, 0.1) > 0.05);
        Assert.True(1 - TrustLedger.BinomialCdf(2, (long)low - 1, 0.1) <= 0.05);
    }
}

public class StandingInheritanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly LifecyclePolicy Policy = new()
    {
        Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
        Margin = 0.01, MinimumEffectiveSample = 300, MinimumDwell = TimeSpan.FromDays(7), StableWindows = 3,
    };

    private static LedgerCell Cell() => new()
    {
        TierId = "ml", SliceKey = "s", WindowStart = Start, WindowEnd = Start.AddDays(7),
        Rechecks = 500, UpperFailures = 0, EffectiveSampleSize = 500, Agreement = 0.98, AgreementLower = 0.97, AgreementUpper = 0.99,
        MissesEstimated = 0, Caught = 400, MissRate = 0, MissRateUpper = 0.01, MissRateLower = 0, Confidence = 0.95,
    };

    [Fact]
    public void ARefinedSliceOfAStableParent_IsActive_OneWindowShortOfStable()
    {
        var child = SliceStanding.Refined(new SliceStanding(TrustState.Stable, Start, "v1"), Policy);

        Assert.Equal(TrustState.Active, child.State);
        Assert.Equal(Start, child.Since);                                       // the dwell is inherited
        var step = TrustLifecycle.Release(child, Cell(), true, new LiveSignals("v1"), Policy, Start.AddDays(14));
        Assert.Equal(TrustState.Stable, step.To);                               // one window of its own
    }

    [Fact]
    public void ARefinedSliceOfAShadowOrActiveParent_KeepsItsStanding()
    {
        var active = new SliceStanding(TrustState.Active, Start, "v1", PassingWindows: 1);

        Assert.Equal(active, SliceStanding.Refined(active, Policy));
        Assert.Equal(TrustState.Shadow, SliceStanding.Refined(SliceStanding.New("v1", Start), Policy).State);
    }

    [Fact]
    public void ANewVersionShownNoWorse_StartsActive_NotStable()
    {
        var stable = new SliceStanding(TrustState.Stable, Start, "v1");

        var proven = TrustLifecycle.Tighten(stable, Cell(), new LiveSignals("v2", VersionProvenNoWorse: true), Policy, Start.AddDays(1));
        var unproven = TrustLifecycle.Tighten(stable, Cell(), new LiveSignals("v2"), Policy, Start.AddDays(1));

        Assert.Equal(TrustState.Active, proven.To);
        Assert.Equal("v2", proven.Standing.TierVersion);
        Assert.Equal(TrustState.Shadow, unproven.To);
        // Never above where the old version stood.
        Assert.Equal(TrustState.Shadow, TrustLifecycle.Tighten(SliceStanding.New("v1", Start), Cell(), new LiveSignals("v2", VersionProvenNoWorse: true), Policy, Start).To);
    }
}
