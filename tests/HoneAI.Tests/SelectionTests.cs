using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Routing;
using Xunit;

namespace HoneAI.Tests;

public class SelectionScoreTests
{
    private static readonly RiskBin[] Bins = [new(0.2, 0.01), new(0.6, 0.05), new(1.0, 0.30)];

    [Theory]
    [InlineData(0.0, 0.01)]
    [InlineData(0.2, 0.01)]
    [InlineData(0.21, 0.05)]
    [InlineData(0.9, 0.30)]
    [InlineData(5.0, 0.30)]   // above the last bin: the last bin's rate
    public void Calibrate_ReadsTheBinTheRiskFallsIn(double risk, double expected)
        => Assert.Equal(expected, SelectionScore.Calibrate(Bins, risk));

    [Fact]
    public void Calibrate_DoesNotDependOnTheOrderTheBinsAreGiven()
        => Assert.Equal(0.05, SelectionScore.Calibrate(Bins.Reverse().ToArray(), 0.5));

    [Fact]
    public void Calibrate_WithoutBins_IsUnknown()
        => Assert.Null(SelectionScore.Calibrate([], 0.5));

    [Theory]
    [InlineData(SelectionShape.Variance, 0.25, 3.0, 0.4330127)]          // √(.25·.75), weight ignored
    [InlineData(SelectionShape.WeightedVariance, 0.25, 3.0, 1.2990381)]  // 3·√(.25·.75)
    [InlineData(SelectionShape.WeightedRisk, 0.25, 3.0, 0.75)]           // 3·.25
    public void Score_HasTheShapeOfItsKnob(SelectionShape shape, double p, double w, double expected)
        => Assert.Equal(expected, SelectionScore.Score(shape, p, w), 6);

    [Fact]
    public void Rate_BeforeTheSignalIsCalibrated_IsTheKnobsRate()
    {
        var uncalibrated = new KnobSelection { Shape = SelectionShape.Variance, Calibration = [], MeanScore = 0.1, UniformMix = 0 };

        Assert.Equal(0.05, SelectionScore.Rate(0.05, uncalibrated, new SelectionSignal { Risk = 0.9 }));
        Assert.Equal(0.05, SelectionScore.Rate(0.05, null, new SelectionSignal { Risk = 0.9 }));
    }

    [Fact]
    public void Rate_FullyMixed_IsTheKnobsRate()
    {
        var mixed = new KnobSelection { Shape = SelectionShape.Variance, Calibration = Bins, MeanScore = 0.1, UniformMix = 1 };

        Assert.Equal(0.05, SelectionScore.Rate(0.05, mixed, new SelectionSignal { Risk = 0.9 }), 12);
    }

    [Fact]
    public void Rate_WithoutARisk_IsTheKnobsRate_TimesTheWeightWhereTheShapeWeighs()
    {
        var variance = new KnobSelection { Shape = SelectionShape.Variance, Calibration = Bins, MeanScore = 0.1, UniformMix = 0 };
        var weighted = variance with { Shape = SelectionShape.WeightedVariance };
        var heavy = new SelectionSignal { Weight = 3 };

        Assert.Equal(0.05, SelectionScore.Rate(0.05, variance, heavy), 12);
        Assert.Equal(0.15, SelectionScore.Rate(0.05, weighted, heavy), 12);
    }

    [Fact]
    public void Rate_KeepsTheKnobsExpectedRate_OverTheDecisionsItWasNormalizedOn()
    {
        // The mean score is fitted on the same population: the tilt moves rechecks between
        // decisions, it does not add or remove any.
        var signals = Enumerable.Range(0, 1000).Select(i => new SelectionSignal { Risk = i / 1000.0, Weight = 1 + i % 3 }).ToArray();
        foreach (var shape in Enum.GetValues<SelectionShape>())
        {
            var probe = new KnobSelection { Shape = shape, Calibration = Bins, MeanScore = 1, UniformMix = 0 };
            var mean = signals.Average(s => SelectionScore.ScoreOf(probe, s)!.Value);
            var selection = probe with { MeanScore = mean, UniformMix = 0.2 };

            var expected = signals.Average(s => SelectionScore.Rate(0.05, selection, s));

            Assert.Equal(0.05, expected, 10);
        }
    }

    [Fact]
    public void Rate_IsCappedAtOne()
    {
        var steep = new KnobSelection { Shape = SelectionShape.WeightedRisk, Calibration = Bins, MeanScore = 0.001, UniformMix = 0 };

        Assert.Equal(1.0, SelectionScore.Rate(0.5, steep, new SelectionSignal { Risk = 0.9, Weight = 10 }));
    }
}

public class SelectionRoutingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tier Ml = new() { Id = "edge-ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
    private static readonly Tier Rules = new() { Id = "rules", Kind = JudgeKind.System1, Version = "1", CostRank = 0 };
    private static readonly Tier Person = new() { Id = "reviewer", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };

    private sealed record Item(string Id, double Risk, double Weight = 2);

    private static readonly ISettlementPolicy<string> Policy =
        SettlementPolicy.Categorical(new[] { "OK", "LOW" }, new[] { "HIGH" });

    private static readonly KnobSelection Watch = new()
    {
        Shape = SelectionShape.WeightedVariance,
        Calibration = [new RiskBin(0.5, 0.01), new RiskBin(1.0, 0.25)],
        MeanScore = 0.4,
        UniformMix = 0.2,
    };

    private static PolicySnapshot Snapshot(IReadOnlyDictionary<string, KnobSelection>? selection) => new()
    {
        Version = "snap-1",
        IssuedAt = Now.AddHours(-1),
        SoftExpiresAt = Now.AddHours(1),
        HardExpiresAt = Now.AddHours(2),
        Entries =
        [
            new PolicyEntry
            {
                TierId = Ml.Id, SliceKey = "s1", State = TrustState.Active,
                SampleRates = new Dictionary<string, double> { ["uniform"] = 0.02, ["slice-watch"] = 0.05 },
                Selection = selection,
            },
        ],
    };

    private sealed class RiskSignals : ISelectionSignals<Item, string>
    {
        public List<SelectionContext<string>> Seen { get; } = [];

        public SelectionSignal? SignalOf(Item input, SelectionContext<string> context)
        {
            Seen.Add(context);
            return new SelectionSignal { Risk = input.Risk, Weight = input.Weight, Cluster = "c-" + input.Id };
        }
    }

    private sealed class ThrowingSignals : ISelectionSignals<Item, string>
    {
        public SelectionSignal? SignalOf(Item input, SelectionContext<string> context) => throw new InvalidOperationException("down");
    }

    private static TieredRouter<Item, string> Router(PolicySnapshot snapshot, ISelectionSignals<Item, string>? signals, params IJudge<Item, string>[] judges)
        => new(judges.Length > 0 ? judges : [Judge.Constant<Item, string>(Ml, "OK", 0.8), Judge.Constant<Item, string>(Person, "LOW")],
            Policy, SliceKey.From<Item>("k1", _ => "s1"), i => i.Id, () => snapshot,
            new TieredRouterOptions { Clock = () => Now }, signals);

    [Fact]
    public async Task ARiskyDecision_IsDrawnAtTheTiltedRate_AndItsSignalIsRecorded()
    {
        var router = Router(Snapshot(new Dictionary<string, KnobSelection> { ["slice-watch"] = Watch }), new RiskSignals());

        var sample = (await router.RouteAsync(new Item("d1", 0.9))).Provenance.Sample!;

        // slice-watch: 0.05 × (0.8 × 2·√(.25·.75) ÷ 0.4 + 0.2) ≈ 0.097; the uniform knob is untilted.
        var tilted = 0.05 * (0.8 * 2 * Math.Sqrt(0.25 * 0.75) / 0.4 + 0.2);
        Assert.Equal(tilted, sample.KnobRates!["slice-watch"], 12);
        Assert.Equal(0.02, sample.KnobRates["uniform"]);
        Assert.Equal("slice-watch", sample.Knob);
        Assert.Equal(tilted, sample.Probability, 12);
        Assert.Equal(new SelectionSignal { Risk = 0.9, Weight = 2, Cluster = "c-d1" }, sample.Signal);
    }

    [Fact]
    public async Task AQuietDecision_FallsToTheLargerOfItsKnobs_AndNeverBelowTheFloor()
    {
        var router = Router(Snapshot(new Dictionary<string, KnobSelection> { ["slice-watch"] = Watch }), new RiskSignals());

        var sample = (await router.RouteAsync(new Item("d2", 0.1, Weight: 0.5))).Provenance.Sample!;

        var tilted = 0.05 * (0.8 * 0.5 * Math.Sqrt(0.01 * 0.99) / 0.4 + 0.2);   // ≈ 0.015
        Assert.Equal(tilted, sample.KnobRates!["slice-watch"], 12);
        Assert.Equal("uniform", sample.Knob);
        Assert.Equal(0.02, sample.Probability);
    }

    [Fact]
    public async Task TheSignalSource_SeesEveryVerdictOnTheWayUp()
    {
        var signals = new RiskSignals();
        var router = Router(Snapshot(null), signals,
            Judge.Constant<Item, string>(Rules, "LOW"), Judge.Constant<Item, string>(Ml, "OK", 0.8), Judge.Constant<Item, string>(Person, "LOW"));

        await router.RouteAsync(new Item("d3", 0.5));

        var seen = Assert.Single(signals.Seen);
        Assert.Equal(Ml.Id, seen.Tier.Id);
        Assert.Equal("OK", seen.Value);
        Assert.Equal(0.8, seen.Confidence);
        Assert.Equal(new (string, string?)[] { ("rules", "LOW"), ("edge-ml", "OK") },
            seen.Verdicts.Select(v => (v.Tier.Id, v.Verdict.Value)));
    }

    [Fact]
    public async Task WithoutASelection_TheSignalIsRecordedButDoesNotTilt()
    {
        var router = Router(Snapshot(null), new RiskSignals());

        var sample = (await router.RouteAsync(new Item("d4", 0.9))).Provenance.Sample!;

        Assert.Equal(0.05, sample.Probability);
        Assert.NotNull(sample.Signal);
    }

    [Fact]
    public async Task AFailingSignalSource_DoesNotFailTheDecision()
    {
        var router = Router(Snapshot(new Dictionary<string, KnobSelection> { ["slice-watch"] = Watch }), new ThrowingSignals());

        var prediction = await router.RouteAsync(new Item("d5", 0.9));

        Assert.Equal("OK", prediction.Value);
        Assert.Equal(0.05, prediction.Provenance.Sample!.Probability);
        Assert.Null(prediction.Provenance.Sample.Signal);
    }

    [Fact]
    public async Task ATiltedDraw_StillCountsWithoutBias_AndMorePreciselyThanUniformAtTheSameBudget()
    {
        // 20,000 settled decisions; the tier is wrong with the calibrated probability of its risk bin.
        // The 1/π count of mistakes over the drawn ones is unbiased under either design; selection
        // spends the same expected budget where the mistakes are, so it misses the truth by less.
        var bins = new[] { new RiskBin(0.5, 0.005), new RiskBin(1.0, 0.20) };
        var truth = new Random(7);
        var items = Enumerable.Range(0, 20_000)
            .Select(i => (Id: $"d{i}", Risk: i % 10 == 0 ? 0.9 : 0.1))
            .Select(x => (x.Id, x.Risk, Wrong: truth.NextDouble() < (x.Risk > 0.5 ? 0.20 : 0.005)))
            .ToArray();
        var trueMistakes = items.Count(x => x.Wrong);

        var probe = new KnobSelection { Shape = SelectionShape.Variance, Calibration = bins, MeanScore = 1, UniformMix = 0 };
        var mean = items.Average(x => SelectionScore.ScoreOf(probe, new SelectionSignal { Risk = x.Risk })!.Value);
        var selection = probe with { MeanScore = mean, UniformMix = 0.1 };

        double Error(KnobSelection? knob, string version)
        {
            var estimates = new List<double>();
            for (var replicate = 0; replicate < 40; replicate++)
            {
                var estimate = 0.0;
                foreach (var x in items)
                {
                    var pi = SelectionScore.Rate(0.05, knob, new SelectionSignal { Risk = x.Risk });
                    if (RecheckDraw.IsDrawn(x.Id, $"{version}-{replicate}", pi) && x.Wrong)
                        estimate += 1 / pi;
                }
                estimates.Add(estimate);
            }
            Assert.InRange(estimates.Average(), trueMistakes * 0.9, trueMistakes * 1.1);   // unbiased
            return Math.Sqrt(estimates.Average(e => (e - trueMistakes) * (e - trueMistakes)));
        }

        var uniform = Error(null, "u");
        var selected = Error(selection, "s");

        Assert.True(selected < uniform * 0.8, $"selected RMSE {selected:F1} vs uniform {uniform:F1}");
    }

    [Fact]
    public void TheCanonicalSnapshot_CarriesTheSelection_AndItsHashMovesWithIt()
    {
        var with = Snapshot(new Dictionary<string, KnobSelection> { ["slice-watch"] = Watch });
        var without = Snapshot(null);

        var json = System.Text.Encoding.UTF8.GetString(PolicySnapshotJson.Serialize(with));
        var back = PolicySnapshotJson.Deserialize(PolicySnapshotJson.Serialize(with));

        Assert.Contains("\"selection\":{\"slice-watch\":{\"shape\":\"WeightedVariance\"", json);
        Assert.NotEqual(PolicySnapshotJson.ContentHash(with), PolicySnapshotJson.ContentHash(without));
        Assert.Equal(PolicySnapshotJson.ContentHash(with), PolicySnapshotJson.ContentHash(back));
        var restored = back.Entries[0].Selection!["slice-watch"];
        Assert.Equal(Watch.Shape, restored.Shape);
        Assert.Equal(Watch.Calibration, restored.Calibration);
        Assert.Equal(Watch.MeanScore, restored.MeanScore);
    }
}
