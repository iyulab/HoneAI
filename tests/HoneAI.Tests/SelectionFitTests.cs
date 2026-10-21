using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class SelectionFitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    // Settled decisions with a raw risk in [0, 1]; the tier is wrong with probability wrongAt(risk).
    // A uniform 10% of them is rechecked.
    private static (List<RecheckRecord> Rechecks, List<SelectionSignal> Decided) Population(
        int n, Func<double, double> wrongAt, int seed, double pi = 0.1)
    {
        var random = new Random(seed);
        var rechecks = new List<RecheckRecord>();
        var decided = new List<SelectionSignal>();
        for (var i = 0; i < n; i++)
        {
            var risk = random.NextDouble();
            var wrong = random.NextDouble() < wrongAt(risk);
            decided.Add(new SelectionSignal { Risk = risk, Weight = 1 + i % 2 });
            if (random.NextDouble() >= pi)
                continue;
            rechecks.Add(new RecheckRecord
            {
                DecisionId = $"d{i}", TierId = "ml", SliceKey = "s", DecidedAt = Now, Probability = pi,
                Outcome = wrong ? RecheckOutcome.Disagreed : RecheckOutcome.Agreed, Risk = risk, Weight = 1 + i % 2,
            });
        }
        return (rechecks, decided);
    }

    [Fact]
    public void WithTooLittleEvidence_TheSignalIsNotTrusted()
    {
        var (rechecks, decided) = Population(500, r => 0.1 * r, seed: 1);

        var fit = SelectionFit.Fit(rechecks, decided, SelectionShape.Variance);

        Assert.Empty(fit.Selection.Calibration);
        Assert.Equal(1, fit.Selection.UniformMix);
        Assert.Contains("needed before the signal is trusted", fit.Reason);
        Assert.Equal(0.05, SelectionScore.Rate(0.05, fit.Selection, new SelectionSignal { Risk = 0.9 }));
    }

    [Fact]
    public void AnInformativeSignal_IsCalibratedMonotonically_AndTrustedAboveTheMinimumMix()
    {
        Func<double, double> truth = r => 0.01 + 0.3 * r * r;
        var (rechecks, decided) = Population(20_000, truth, seed: 2);

        var fit = SelectionFit.Fit(rechecks, decided, SelectionShape.WeightedVariance);

        var bins = fit.Selection.Calibration;
        Assert.True(bins.Count >= 3, $"{bins.Count} bins");
        for (var i = 1; i < bins.Count; i++)
            Assert.True(bins[i].Probability >= bins[i - 1].Probability, "the calibration never falls as risk rises");
        Assert.InRange(SelectionScore.Calibrate(bins, 0.05)!.Value, 0.0, 0.06);    // truth ≈ 0.01–0.02 at the low end
        Assert.InRange(SelectionScore.Calibrate(bins, 0.97)!.Value, 0.15, 0.40);   // truth ≈ 0.25–0.31 at the high end
        Assert.True(fit.HeldOutRankAuc > 0.65, $"held-out AUC {fit.HeldOutRankAuc}");
        Assert.InRange(fit.Selection.UniformMix, 0.1, 0.7);
    }

    [Fact]
    public void ASignalUnrelatedToMistakes_IsDrawnUniformly()
    {
        var (rechecks, decided) = Population(20_000, _ => 0.05, seed: 3);

        var fit = SelectionFit.Fit(rechecks, decided, SelectionShape.Variance);

        Assert.True(fit.Selection.UniformMix >= 0.8, $"τ {fit.Selection.UniformMix} on a meaningless signal ({fit.Reason})");
    }

    [Fact]
    public void ASmallBin_DoesNotClaimZero()
    {
        // No disagreement at all below risk 0.5.
        var (rechecks, decided) = Population(20_000, r => r < 0.5 ? 0 : 0.2, seed: 4);

        var fit = SelectionFit.Fit(rechecks, decided, SelectionShape.Variance);

        Assert.All(fit.Selection.Calibration, b => Assert.True(b.Probability > 0, $"bin up to {b.UpTo} claims {b.Probability}"));
    }

    [Fact]
    public void TheMeanScore_KeepsTheKnobsExpectedRate_OverTheDecisionsItWasFittedOn()
    {
        var (rechecks, decided) = Population(20_000, r => 0.01 + 0.3 * r * r, seed: 5);
        // A fifth of the decisions carry no risk, only a weight.
        decided = decided.Select((s, i) => i % 5 == 0 ? s with { Risk = null } : s).ToList();

        foreach (var shape in Enum.GetValues<SelectionShape>())
        {
            var selection = SelectionFit.Fit(rechecks, decided, shape).Selection;

            var expected = decided.Average(s => SelectionScore.Rate(0.02, selection, s));

            Assert.Equal(0.02, expected, 6);
        }
    }

    [Fact]
    public void FailedRechecksAndRecordsWithoutARisk_AreNotFittedOn()
    {
        var (rechecks, decided) = Population(20_000, r => 0.01 + 0.3 * r * r, seed: 6);
        var noise = Enumerable.Range(0, 5_000).Select(i => new RecheckRecord
        {
            DecisionId = $"x{i}", TierId = "ml", SliceKey = "s", DecidedAt = Now, Probability = 0.1,
            Outcome = i % 2 == 0 ? RecheckOutcome.UpperFailed : RecheckOutcome.Disagreed, Risk = i % 2 == 0 ? 0.0 : null,
        });

        var clean = SelectionFit.Fit(rechecks, decided, SelectionShape.Variance);
        var withNoise = SelectionFit.Fit(rechecks.Concat(noise), decided, SelectionShape.Variance);

        Assert.Equal(clean.Rechecks, withNoise.Rechecks);
        Assert.Equal(clean.Selection.Calibration, withNoise.Selection.Calibration);
    }

    [Fact]
    public void ARepeatedCluster_IsDampedToItsCap_AndTheKnobKeepsItsRate()
    {
        // Half the decisions are one cluster of near-identical risky inputs; at a 5% knob the tilt
        // would send it most of its rechecks. Capped at 60, it is thinned and the rest gain.
        var (rechecks, decided) = Population(20_000, r => 0.01 + 0.3 * r * r, seed: 7);
        decided = decided.Select((s, i) => i % 2 == 0
            ? s with { Risk = 0.95, Cluster = "same-template" }
            : s with { Cluster = $"c{i % 40}" }).ToList();
        var options = new SelectionFitOptions { ClusterRecheckCap = 60, KnobRate = 0.05 };

        var selection = SelectionFit.Fit(rechecks, decided, SelectionShape.WeightedVariance, options).Selection;

        var factor = selection.ClusterFactors!["same-template"];
        Assert.InRange(factor, 0.05, 0.5);
        // The tilt sends it the cap; the uniform share it gets anyway is proportional to its size.
        var members = decided.Where(d => d.Cluster == "same-template").ToList();
        var draws = members.Sum(d => SelectionScore.Rate(0.05, selection, d));
        var uniformShare = members.Count * 0.05 * selection.UniformMix;
        Assert.InRange(draws - uniformShare, 57, 63);
        Assert.Equal(0.05, decided.Average(d => SelectionScore.Rate(0.05, selection, d)), 3);
    }

    [Fact]
    public void AClusterCap_NeedsTheKnobsRate()
    {
        var (rechecks, decided) = Population(20_000, r => 0.01 + 0.3 * r * r, seed: 8);

        Assert.Throws<ArgumentException>(() =>
            SelectionFit.Fit(rechecks, decided, SelectionShape.Variance, new SelectionFitOptions { ClusterRecheckCap = 10 }));
    }

    [Fact]
    public async Task TheRecheckRecord_CarriesTheSignal_TheFitReads()
    {
        var tier = new Tier { Id = "ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
        var top = new Tier { Id = "top", Kind = JudgeKind.Human, Version = "1", CostRank = 2 };
        var policy = SettlementPolicy.Categorical(new[] { "OK" }, Array.Empty<string>());
        var snapshot = new PolicySnapshot
        {
            Version = "v", IssuedAt = Now, SoftExpiresAt = Now.AddHours(1), HardExpiresAt = Now.AddHours(2),
            Entries = [new PolicyEntry { TierId = "ml", SliceKey = "s", State = TrustState.Active, SampleRates = new Dictionary<string, double> { ["uniform"] = 1.0 } }],
        };
        var router = new TieredRouter<string, string>(
            [Judge.Constant<string, string>(tier, "OK", 0.9), Judge.Constant<string, string>(top, "OK")],
            policy, SliceKey.From<string>("k", _ => "s"), id => id, () => snapshot,
            new TieredRouterOptions { Clock = () => Now }, new FixedSignal(new SelectionSignal { Risk = 0.42, Weight = 3 }));

        var decision = await router.DecideAsync("d1");
        var recheck = await router.RecheckAsync("d1", "d1", "top");
        var record = RecheckRecords.From(decision, recheck, policy);

        Assert.Equal(0.42, record.Risk);
        Assert.Equal(3, record.Weight);
    }

    private sealed class FixedSignal(SelectionSignal signal) : ISelectionSignals<string, string>
    {
        public SelectionSignal? SignalOf(string input, SelectionContext<string> context) => signal;
    }
}
