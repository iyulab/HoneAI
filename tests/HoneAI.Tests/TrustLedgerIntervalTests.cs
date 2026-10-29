using System;
using System.Collections.Generic;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// The agreement interval's coverage, measured by drawing windows from known populations and counting how often the
/// interval holds the true agreement. The draws are seeded, so a failure is a changed interval, not bad luck.
/// </summary>
public class TrustLedgerIntervalTests
{
    private static readonly DateTimeOffset Decided = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static RecheckRecord Record(int i, double probability, bool agreed) => new()
    {
        DecisionId = "d" + i,
        TierId = "tier",
        SliceKey = "slice",
        DecidedAt = Decided,
        Probability = probability,
        Outcome = agreed ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed,
    };

    private static LedgerCell Compute(IEnumerable<RecheckRecord> records)
        => TrustLedger.Compute(records, "tier", "slice", Decided.AddDays(-1), Decided.AddDays(1));

    /// <summary>
    /// Windows of 4,000 decisions whose chance of disagreeing runs with x and whose recheck probability runs with x (or against it).
    /// Returns the share of windows whose interval holds the true agreement, and the share whose lower bound lies above it
    /// (the share a release would be wrongly allowed on).
    /// </summary>
    private static (double Covered, double LowerAbove) Coverage(double agreement, double expectedRechecks, double tilt, bool towardRisk, int windows, int seed)
    {
        const int decisions = 4000;
        var rng = new Random(seed);
        var mean = expectedRechecks / decisions;
        var low = 2 * mean / (1 + tilt);
        var high = tilt * low;
        int covered = 0, lowerAbove = 0;
        for (var w = 0; w < windows; w++)
        {
            var records = new List<RecheckRecord>();
            for (var i = 0; i < decisions; i++)
            {
                var x = rng.NextDouble();
                var disagree = 2 * (1 - agreement) * (towardRisk ? x : 1 - x);
                var agreed = rng.NextDouble() >= disagree;
                var probability = low + (high - low) * x;
                if (rng.NextDouble() < probability)
                    records.Add(Record(i, probability, agreed));
            }
            var cell = Compute(records);
            if (cell.AgreementLower <= agreement && agreement <= cell.AgreementUpper)
                covered++;
            if (cell.AgreementLower > agreement)
                lowerAbove++;
        }
        return ((double)covered / windows, (double)lowerAbove / windows);
    }

    [Fact]
    public void WithEqualWeights_TheIntervalIsWilsonsOnTheNumberOfRechecks()
    {
        var records = new List<RecheckRecord>();
        for (var i = 0; i < 200; i++)
            records.Add(Record(i, 0.1, agreed: i % 10 != 0));

        var cell = Compute(records);
        var z = TrustLedger.NormalQuantile(0.975);
        var expected = TrustLedger.Wilson(0.9, 200, z);

        Assert.Equal(expected.Lower, cell.AgreementLower!.Value, 9);
        Assert.Equal(expected.Upper, cell.AgreementUpper!.Value, 9);
    }

    [Fact]
    public void WhenOutcomesVaryMostWhereTheWeightIsHeaviest_TheIntervalIsWiderThanKishSizeWouldMakeIt()
    {
        // Most of the weight sits on 24 rechecks of heavy weight, and the disagreements are among them: Kish's size from the
        // weights alone is 32, but the ratio's own variance says the window is worth less than that.
        var records = new List<RecheckRecord>();
        for (var i = 0; i < 100; i++)
            records.Add(Record(i, 0.5, agreed: true));
        for (var i = 100; i < 104; i++)
            records.Add(Record(i, 0.02, agreed: false));
        for (var i = 104; i < 124; i++)
            records.Add(Record(i, 0.02, agreed: true));

        var cell = Compute(records);
        var kish = TrustLedger.EffectiveSize(records.ConvertAll(r => 1 / r.Probability));
        var onKish = TrustLedger.Wilson(cell.Agreement!.Value, kish, TrustLedger.NormalQuantile(0.975));

        Assert.True(cell.AgreementLower < onKish.Lower, $"lower {cell.AgreementLower} against {onKish.Lower} on Kish size {kish:F0}");
        Assert.True(cell.AgreementUpper > onKish.Upper, $"upper {cell.AgreementUpper} against {onKish.Upper}");
    }

    [Fact]
    public void AWindowWithNoVariation_KeepsKishSize()
    {
        var records = new List<RecheckRecord>();
        for (var i = 0; i < 50; i++)
            records.Add(Record(i, i % 2 == 0 ? 0.1 : 0.4, agreed: true));

        var cell = Compute(records);
        var kish = TrustLedger.EffectiveSize(records.ConvertAll(r => 1 / r.Probability));

        Assert.Equal(1.0, cell.Agreement!.Value, 9);
        Assert.Equal(TrustLedger.Wilson(1.0, kish, TrustLedger.NormalQuantile(0.975)).Lower, cell.AgreementLower!.Value, 9);
    }

    [Theory]
    [InlineData(0.90, 100, 1.0)]
    [InlineData(0.95, 100, 4.0)]
    [InlineData(0.90, 400, 20.0)]
    public void DrawnTowardTheRiskyOnes_TheIntervalHoldsTheTruthAtLeastNineteenInTwenty(double agreement, double rechecks, double tilt)
    {
        var (covered, _) = Coverage(agreement, rechecks, tilt, towardRisk: true, windows: 600, seed: 11);
        Assert.InRange(covered, 0.93, 1.0);
    }

    [Theory]
    [InlineData(0.90, 100, 4.0)]
    [InlineData(0.90, 400, 20.0)]
    [InlineData(0.80, 100, 20.0)]
    public void DrawnAwayFromTheRiskyOnes_TheIntervalStillHoldsTheTruthNearlyAsOftenAsStated(double agreement, double rechecks, double tilt)
    {
        // On Kish size alone these cells covered as few as 0.906 of windows and the lower bound lay above the truth in up to
        // 4.2 %; on the variance of the ratio coverage is at least 0.939 and a release is wrongly allowed in at most ~3.6 %
        // (the bounds below leave room for the 600-window sample).
        var (covered, lowerAbove) = Coverage(agreement, rechecks, tilt, towardRisk: false, windows: 600, seed: 12);
        Assert.InRange(covered, 0.92, 1.0);
        Assert.InRange(lowerAbove, 0.0, 0.05);
    }
}
