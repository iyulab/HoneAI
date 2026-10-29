using System;
using System.Collections.Generic;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// A tier judged for tightening on a cell that grows with the window is tested again at every look. These measure what that
/// does to the chance of tightening a tier that meets its bar, and what <see cref="TrustLedger.ConfidenceForLooks"/> restores.
/// </summary>
public class RunningLooksTests
{
    private static readonly DateTimeOffset Decided = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly LifecyclePolicy Policy = new()
    {
        Targets = new TrustTargets { Agreement = 0.90, DetectableDrop = 0.04, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
        Margin = 0.02,
        MinimumEffectiveSample = 50,
        MinimumDwell = TimeSpan.FromDays(1),
    };

    [Theory]
    [InlineData(1, 0.95, 0.95)]
    [InlineData(10, 0.95, 0.995)]
    [InlineData(50, 0.95, 0.999)]
    [InlineData(20, 0.99, 0.9995)]
    public void TheCorrectedConfidence_SpreadsTheFamilyRiskOverTheLooks(int looks, double family, double expected)
        => Assert.Equal(expected, TrustLedger.ConfidenceForLooks(looks, family), 12);

    [Theory]
    [InlineData(0, 0.95)]
    [InlineData(5, 0.0)]
    [InlineData(5, 1.0)]
    public void ConfidenceForLooks_RefusesWhatIsNotALookOrAConfidence(int looks, double family)
        => Assert.Throws<ArgumentOutOfRangeException>(() => TrustLedger.ConfidenceForLooks(looks, family));

    /// <summary>Share of windows in which any of <paramref name="looks"/> looks tightens a tier whose true agreement is <paramref name="agreement"/>.</summary>
    private static double TightenedShare(double agreement, int looks, double confidence, int windows, int seed)
    {
        var rng = new Random(seed);
        var standing = new SliceStanding(TrustState.Active, Decided.AddDays(-30), "v1");
        var signals = new LiveSignals("v1");
        var rate = RecheckRate.Solve(Policy, new SliceTraffic(10000, 5000)).Rate;
        var rechecks = (int)Math.Round(rate * 10000);
        var tightened = 0;
        for (var w = 0; w < windows; w++)
        {
            var records = new List<RecheckRecord>();
            for (var look = 1; look <= looks; look++)
            {
                var upto = (int)Math.Round((double)rechecks * look / looks);
                while (records.Count < upto)
                    records.Add(new RecheckRecord
                    {
                        DecisionId = "d" + records.Count,
                        TierId = "t",
                        SliceKey = "s",
                        DecidedAt = Decided,
                        Probability = rate,
                        Outcome = rng.NextDouble() < agreement ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed,
                    });
                var cell = TrustLedger.Compute(records, "t", "s", Decided.AddDays(-1), Decided.AddDays(1), caught: 5000, confidence: confidence);
                if (TrustLifecycle.Tighten(standing, cell, signals, Policy, Decided).To == TrustState.Shadow)
                {
                    tightened++;
                    break;
                }
            }
        }
        return (double)tightened / windows;
    }

    [Fact]
    public void FiftyLooksAtTheDefaultConfidence_TightenATierThatMeetsItsBar_FarMoreOftenThanFiveInAHundred()
    {
        // The tier is at the tightening bar (0.88), where a test at 5 % two-sided is meant to fire 2.5 % of the time on one look.
        var share = TightenedShare(0.88, looks: 50, TrustLedger.DefaultConfidence, windows: 400, seed: 21);
        Assert.InRange(share, 0.10, 1.0);
    }

    [Fact]
    public void FiftyLooksAtTheCorrectedConfidence_TightenATierAtItsBar_WithinTheFamilyRisk()
    {
        var share = TightenedShare(0.88, looks: 50, TrustLedger.ConfidenceForLooks(50), windows: 400, seed: 22);
        Assert.InRange(share, 0.0, 0.05);
    }

    [Fact]
    public void FiftyLooksAtTheCorrectedConfidence_RarelyTightenATierThatKeepsItsPromise()
    {
        var share = TightenedShare(0.90, looks: 50, TrustLedger.ConfidenceForLooks(50), windows: 400, seed: 23);
        Assert.InRange(share, 0.0, 0.02);
    }
}
