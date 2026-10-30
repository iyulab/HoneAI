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
    [InlineData(1, 0.99, 0.99)]
    public void OneLook_IsTheFamilyConfidence(int looks, double family, double expected)
        => Assert.Equal(expected, TrustLedger.ConfidenceForLooks(looks, family), 12);

    /// <summary>The interval half-width multiplier a confidence stands for, as <see cref="TrustLedger.Compute"/> reads it.</summary>
    private static double Z(double confidence) => TrustLedger.NormalQuantile(1 - (1 - confidence) / 2);

    [Theory]
    // Pocock's published constants for a two-sided 5 % family.
    [InlineData(2, 2.178)]
    [InlineData(5, 2.413)]
    [InlineData(10, 2.555)]
    public void TheCorrectedConfidence_IsPococksConstantBoundary(int looks, double expectedZ)
        => Assert.Equal(expectedZ, Z(TrustLedger.ConfidenceForLooks(looks, 0.95)), 2);

    [Theory]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void TheCorrectedConfidence_LiesBetweenOneLookAndTheUnionBound_AndRisesWithTheLooks(double family)
    {
        var previous = family;
        foreach (var looks in new[] { 2, 3, 5, 8, 12, 20, 50, 100 })
        {
            var confidence = TrustLedger.ConfidenceForLooks(looks, family);
            Assert.True(confidence > previous, $"{looks} looks: {confidence} should exceed {previous}");
            Assert.True(confidence <= 1 - (1 - family) / looks + 1e-9, $"{looks} looks: {confidence} is wider than the union bound");
            previous = confidence;
        }
    }

    [Fact]
    public void AboveTheExactLimit_TheUnionBoundIsUsed()
        => Assert.Equal(1 - 0.05 / 500, TrustLedger.ConfidenceForLooks(500, 0.95), 12);

    [Theory]
    [InlineData(0, 0.95)]
    [InlineData(5, 0.0)]
    [InlineData(5, 1.0)]
    public void ConfidenceForLooks_RefusesWhatIsNotALookOrAConfidence(int looks, double family)
        => Assert.Throws<ArgumentOutOfRangeException>(() => TrustLedger.ConfidenceForLooks(looks, family));

    /// <summary>Share of windows in which any of <paramref name="looks"/> looks tightens a tier whose true agreement is <paramref name="agreement"/>.</summary>
    private static double TightenedShare(double agreement, int looks, double confidence, int windows, int seed)
    {
        var standing = new SliceStanding(TrustState.Active, Decided.AddDays(-30), "v1");
        var signals = new LiveSignals("v1");
        var rate = RecheckRate.Solve(Policy, new SliceTraffic(10000, 5000)).Rate;
        var rechecks = (int)Math.Round(rate * 10000);
        var tightened = 0;
        for (var w = 0; w < windows; w++)
        {
            var rng = new Random(seed * 100_003 + w);
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

    [Fact]
    public void TwelveLooks_AtTheConstantBoundary_CatchADropTheUnionBoundMisses_WithinTheSameFamilyRisk()
    {
        const int looks = 12;
        var constant = TrustLedger.ConfidenceForLooks(looks);
        var union = 1 - (1 - TrustLedger.DefaultConfidence) / looks;

        // The same records at each rule: a wider interval cannot tighten a window the narrower one leaves alone.
        Assert.True(TightenedShare(0.84, looks, constant, windows: 300, seed: 31) >= TightenedShare(0.84, looks, union, windows: 300, seed: 31));
        Assert.InRange(TightenedShare(0.88, looks, constant, windows: 400, seed: 32), 0.0, 0.05);
    }
}
