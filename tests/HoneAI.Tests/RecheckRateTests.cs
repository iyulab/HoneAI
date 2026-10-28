using System;
using System.Linq;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class RecheckRateTests
{
    private static readonly TrustTargets Targets = new()
    {
        Agreement = 0.95,
        DetectableDrop = 0.03,
        MissLimit = 0.02,
        Deadline = TimeSpan.FromDays(7),
    };

    private static readonly LifecyclePolicy Policy = new()
    {
        Targets = Targets,
        Margin = 0.01,
        MinimumEffectiveSample = 300,
        MinimumDwell = TimeSpan.FromDays(7),
    };

    [Fact]
    public void TheAgreementTests_NeedTheSampleSizeOfTheBarsTheLifecycleJudgesBy()
    {
        // Release: bar 0.96, a tier at 0.99:  (1.960·√(0.96·0.04) + 0.842·√(0.99·0.01))² / 0.03² ≈ 243, 276 with the continuity correction
        Assert.InRange(RecheckRate.RechecksToRelease(Policy), 275, 276);
        // Tightening: bar 0.94, a tier at 0.91:  (1.960·√(0.94·0.06) + 0.842·√(0.91·0.09))² / 0.03² ≈ 554, 587 corrected
        Assert.InRange(RecheckRate.RechecksToTighten(Policy), 587, 588);
        Assert.Equal(RecheckRate.RechecksToTighten(Policy), RecheckRate.RechecksForAgreement(Policy));
    }

    [Fact]
    public void TheSampleForRelease_ReleasesATierAboveTheBar_WithTheAskedPower()
    {
        // The release test's own arithmetic, summed exactly over the binomial: the sample the formula names must
        // clear the Wilson lower bound at the bar for at least `Power` of the windows of a tier δ above it.
        var n = (int)Math.Ceiling(RecheckRate.RechecksToRelease(Policy));
        var bar = Targets.Agreement + Policy.Margin;
        var z = TrustLedger.NormalQuantile(1 - Targets.Alpha / 2);

        var power = BinomialMass(n, bar + Targets.DetectableDrop, k => TrustLedger.Wilson((double)k / n, n, z).Lower >= bar);

        Assert.True(power >= Targets.Power, $"released {power:P1} of windows with n = {n}");
    }

    [Fact]
    public void TheSampleForTightening_TightensATierBelowTheBar_WithTheAskedPower()
    {
        var n = (int)Math.Ceiling(RecheckRate.RechecksToTighten(Policy));
        var bar = Targets.Agreement - Policy.Margin;
        var z = TrustLedger.NormalQuantile(1 - Targets.Alpha / 2);

        var power = BinomialMass(n, bar - Targets.DetectableDrop, k => TrustLedger.Wilson((double)k / n, n, z).Upper < bar);

        Assert.True(power >= Targets.Power, $"tightened {power:P1} of windows with n = {n}");
    }

    [Fact]
    public void TheOldSample_DidNotReleaseATierAtThePromisePlusTheDrop()
    {
        // The sample that sized the old term (a one-sided test of a drop from p0, 326 here) released a tier δ above
        // p0 about half the time — the regression this pins: the sample must come from the release test itself.
        const int old = 326;
        var bar = Targets.Agreement + Policy.Margin;
        var z = TrustLedger.NormalQuantile(1 - Targets.Alpha / 2);

        var power = BinomialMass(old, Targets.Agreement + Targets.DetectableDrop, k => TrustLedger.Wilson((double)k / old, old, z).Lower >= bar);

        Assert.InRange(power, 0.4, 0.6);
    }

    [Fact]
    public void AnAbsoluteAllowance_SizesTheMissTerm_WhereNothingWasCaught()
    {
        var policy = Policy with { Targets = Targets with { HiddenMustCatchLimit = 30 } };

        var choice = RecheckRate.Solve(policy, new SliceTraffic(Decisions: 1_000_000, Caught: 0));

        // ln 20 / 30 ≈ 0.0999: zero misses at that π bound the hidden count by 30.
        Assert.Equal(RateTerm.Miss, choice.SetBy);
        Assert.Equal(Math.Log(20) / 30, choice.MissTerm, 9);
        Assert.False(choice.Capped);
    }

    [Fact]
    public void TheLargerAllowance_Wins_AndWithNoLimitTheTermIsTheRatesOwn()
    {
        // 3,000 caught at m = 2 % allow 61.2 hidden; a limit of 10 does not shrink that, one of 100 grows it.
        var caught = new SliceTraffic(Decisions: 20_000, Caught: 3_000);
        var rate = RecheckRate.Solve(Policy, caught).MissTerm;

        Assert.Equal(rate, RecheckRate.Solve(Policy with { Targets = Targets with { HiddenMustCatchLimit = 10 } }, caught).MissTerm, 12);
        Assert.Equal(Math.Log(20) / 100, RecheckRate.Solve(Policy with { Targets = Targets with { HiddenMustCatchLimit = 100 } }, caught).MissTerm, 12);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void AHiddenLimitThatIsNotAFiniteCount_IsRefused(double limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RecheckRate.Solve(Policy with { Targets = Targets with { HiddenMustCatchLimit = limit } }, new SliceTraffic(10, 10)));

    [Fact]
    public void ABarTheTierCannotClear_NeedsEverythingAndStaysUp()
    {
        var policy = Policy with { Targets = Targets with { Agreement = 0.99 } };   // 0.99 + 0.01 + 0.03 > 1

        Assert.True(double.IsPositiveInfinity(RecheckRate.RechecksToRelease(policy)));
        var choice = RecheckRate.Solve(policy, new SliceTraffic(Decisions: 1_000_000, Caught: 1_000_000));
        Assert.True(choice.Capped);
        Assert.Equal(RateTerm.Agreement, choice.SetBy);
        Assert.Equal(1.0, choice.Rate);
    }

    // P(window passes) when each of n rechecks agrees with probability p.
    private static double BinomialMass(int n, double p, Func<int, bool> passes)
    {
        // In log space: (1 − p)ⁿ alone underflows a double for the sample sizes and agreements here.
        var logMass = n * Math.Log(1 - p);
        var logOdds = Math.Log(p / (1 - p));
        var total = 0.0;
        for (var k = 0; k <= n; k++)
        {
            if (passes(k)) total += Math.Exp(logMass);
            logMass += Math.Log((n - k) / (double)(k + 1)) + logOdds;
        }
        return total;
    }

    [Fact]
    public void ABigSliceWithCommonRisk_IsSetByWhicheverTermNeedsMore()
    {
        var choice = RecheckRate.Solve(Policy, new SliceTraffic(Decisions: 20_000, Caught: 3_000));

        // miss: ln 20 · 0.98 / (0.02 · 3000) ≈ 0.049; agreement: 587 / 20000 ≈ 0.029
        Assert.Equal(RateTerm.Miss, choice.SetBy);
        Assert.Equal(0.0489, choice.Rate, 3);
        Assert.False(choice.Capped);
    }

    [Fact]
    public void WithFewDecisions_TheAgreementTestSetsTheRate()
    {
        var choice = RecheckRate.Solve(Policy, new SliceTraffic(Decisions: 1_000, Caught: 3_000));

        Assert.Equal(RateTerm.Agreement, choice.SetBy);
        Assert.Equal(0.587, choice.Rate, 3);
    }

    [Fact]
    public void ASliceWhereRiskIsRare_IsCapped_AndStaysUp()
    {
        // Zero misses cannot show 2% until about 147 must-catch decisions were caught in the window.
        Assert.False(RecheckRate.Solve(Policy, new SliceTraffic(100_000, 147)).Capped);
        var rare = RecheckRate.Solve(Policy, new SliceTraffic(100_000, 146));
        Assert.True(rare.Capped);
        Assert.Equal(1.0, rare.Rate);

        var none = RecheckRate.Solve(Policy, new SliceTraffic(100_000, 0));
        Assert.True(none.Capped);
        Assert.True(double.IsPositiveInfinity(none.MissTerm));
    }

    [Fact]
    public void TheRate_NeverFallsBelowTheFloor()
    {
        var easy = Policy with { Targets = Targets with { Agreement = 0.5, MissLimit = 0.5, DetectableDrop = 0.4 } };

        var choice = RecheckRate.Solve(easy, new SliceTraffic(1_000_000, 1_000_000));

        Assert.Equal(RateTerm.Floor, choice.SetBy);
        Assert.Equal(easy.Targets.RateFloor, choice.Rate);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void TargetsOutsideTheOpenInterval_AreRefused(double missLimit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RecheckRate.Solve(Policy with { Targets = Targets with { MissLimit = missLimit } }, new SliceTraffic(10, 10)));

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void AMarginOutsideZeroToOne_IsRefused(double margin)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RecheckRate.Solve(Policy with { Margin = margin }, new SliceTraffic(10, 10)));

    [Fact]
    public void AZeroFloor_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RecheckRate.Solve(Policy with { Targets = Targets with { RateFloor = 0 } }, new SliceTraffic(10, 10)));
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
