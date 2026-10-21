using System;
using System.Linq;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class RecheckRateTests
{
    private static readonly TrustTargets Aims = new()
    {
        Agreement = 0.95,
        DetectableDrop = 0.03,
        MissLimit = 0.02,
        Deadline = TimeSpan.FromDays(7),
    };

    [Fact]
    public void TheAgreementTest_NeedsTheTextbookSampleSize()
    {
        // (1.645 + 0.842)² · 0.95 · 0.05 / 0.03² ≈ 326
        Assert.Equal(326.3, RecheckRate.RechecksForAgreement(Aims), 0);
    }

    [Fact]
    public void ABigSliceWithCommonRisk_IsSetByWhicheverTermNeedsMore()
    {
        var choice = RecheckRate.Solve(Aims, new SliceTraffic(Decisions: 20_000, Caught: 3_000));

        // miss: ln 20 · 0.98 / (0.02 · 3000) ≈ 0.049; agreement: 326 / 20000 ≈ 0.016
        Assert.Equal(RateTerm.Miss, choice.SetBy);
        Assert.Equal(0.0489, choice.Rate, 3);
        Assert.False(choice.Capped);
    }

    [Fact]
    public void WithFewDecisions_TheAgreementTestSetsTheRate()
    {
        var choice = RecheckRate.Solve(Aims, new SliceTraffic(Decisions: 1_000, Caught: 3_000));

        Assert.Equal(RateTerm.Agreement, choice.SetBy);
        Assert.Equal(0.326, choice.Rate, 3);
    }

    [Fact]
    public void ASliceWhereRiskIsRare_IsCapped_AndStaysUp()
    {
        // Zero misses cannot show 2% until about 147 must-catch decisions were caught in the window.
        Assert.False(RecheckRate.Solve(Aims, new SliceTraffic(100_000, 147)).Capped);
        var rare = RecheckRate.Solve(Aims, new SliceTraffic(100_000, 146));
        Assert.True(rare.Capped);
        Assert.Equal(1.0, rare.Rate);

        var none = RecheckRate.Solve(Aims, new SliceTraffic(100_000, 0));
        Assert.True(none.Capped);
        Assert.True(double.IsPositiveInfinity(none.MissTerm));
    }

    [Fact]
    public void TheRate_NeverFallsBelowTheFloor()
    {
        var easy = Aims with { MissLimit = 0.5, DetectableDrop = 0.5 };

        var choice = RecheckRate.Solve(easy, new SliceTraffic(1_000_000, 1_000_000));

        Assert.Equal(RateTerm.Floor, choice.SetBy);
        Assert.Equal(easy.RateFloor, choice.Rate);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void TargetsOutsideTheOpenInterval_AreRefused(double missLimit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RecheckRate.Solve(Aims with { MissLimit = missLimit }, new SliceTraffic(10, 10)));

    [Fact]
    public void AZeroFloor_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RecheckRate.Solve(Aims with { RateFloor = 0 }, new SliceTraffic(10, 10)));
}

public class RecheckBudgetTests
{
    private static RecheckRateChoice Need(double rate) => new(rate, RateTerm.Miss, rate, 0, false);

    [Fact]
    public void EnoughBudget_FundsEverySlice()
    {
        var allocation = RecheckBudget.Allocate(1_000,
            [new BudgetRequest("a", 1_000, Need(0.2), 1.0), new BudgetRequest("b", 2_000, Need(0.1), 3.0)], rateFloor: 0.01);

        Assert.All(allocation.Grants, g => Assert.True(g.Funded));
        Assert.Equal(400, allocation.Spent, 6);
        Assert.False(allocation.OverBudget);
    }

    [Fact]
    public void AShortPool_FundsTheLargestPossibleMissesFirst_ANewSliceAboveAll()
    {
        var allocation = RecheckBudget.Allocate(300,
        [
            new BudgetRequest("known-small", 1_000, Need(0.2), MissExposure: 0.5),
            new BudgetRequest("known-large", 1_000, Need(0.2), MissExposure: 4.0),
            new BudgetRequest("new", 1_000, Need(0.2), MissExposure: null),
        ], rateFloor: 0.01);

        var grant = allocation.Grants.ToDictionary(g => g.SliceKey);
        Assert.True(grant["new"].Funded);                       // 10 floor + 190
        Assert.False(grant["known-large"].Funded);              // 10 floor + the remaining 80
        Assert.Equal(0.09, grant["known-large"].Rate, 6);
        Assert.Equal(RateTerm.Budget, grant["known-large"].SetBy);
        Assert.Equal(0.01, grant["known-small"].Rate, 6);       // floor only
        Assert.Equal(300, allocation.Spent, 6);
    }

    [Fact]
    public void TheFloor_IsKept_EvenWhenItOverrunsThePool()
    {
        var allocation = RecheckBudget.Allocate(5, [new BudgetRequest("a", 1_000, Need(0.2), null)], rateFloor: 0.01);

        Assert.Equal(0.01, allocation.Grants[0].Rate, 6);
        Assert.True(allocation.OverBudget);
        Assert.False(allocation.Grants[0].Funded);
    }

    [Fact]
    public void ASliceAskingTwice_IsRefused()
        => Assert.Throws<ArgumentException>(() => RecheckBudget.Allocate(10,
            [new BudgetRequest("a", 1, Need(0.5), null), new BudgetRequest("a", 1, Need(0.5), null)], 0.01));
}
