using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI;
using HoneAI.Learning;
using Xunit;

namespace HoneAI.Tests;

public class TrainingSetSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "exp-3", CostRank = 1 };
    private static readonly Tier Person = new() { Id = "person-a", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };
    private static readonly ISettlementPolicy<string> Policy = SettlementPolicy.Categorical(settleable: ["ok", "minor"], mustCatch: ["danger"]);

    // A label of a decision in `slice`, in the near-duplicate group `unit` (null: its own).
    private static ConfirmedLabel<string> Label(string id, string slice, string? unit = null)
        => LabelConfirmation.Decide(id,
                new PredictionProvenance { Kind = JudgeKind.Human, TierId = "person-a", SliceKey = slice },
                [new TracedPrediction<string>("ok", new PredictionProvenance { Kind = Person.Kind, TierId = Person.Id, TierVersion = Person.Version })],
                Ml, [Ml, Person], Policy, _ => null, Now).Label! with
        { SplitKey = unit };

    private static List<ConfirmedLabel<string>> Many(string slice, int count, Func<int, string?>? unit = null)
        => Enumerable.Range(0, count).Select(i => Label($"{slice}-{i}", slice, unit?.Invoke(i))).ToList();

    [Fact]
    public void ANearDuplicateCap_KeepsAFewPerUnit_AndSaysWhatItLeftOut()
    {
        var labels = Many("s", 100, i => $"u{i % 10}");                           // ten units of ten
        var selection = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxPerUnit = 2 });

        Assert.Equal(20, selection.Labels.Count);
        Assert.All(selection.Labels.GroupBy(l => l.Unit), g => Assert.Equal(2, g.Count()));
        Assert.Equal((100, 20, 10), (selection.Manifest.Offered, selection.Manifest.Labels, selection.Manifest.Units));
        Assert.Equal(80, selection.Manifest.Dropped!["unit"]);
        Assert.All(selection.Labels, l => Assert.Equal(1.0, l.SelectionProbability));   // a cap rebalances; it is not compensated
        Assert.Equal(20, selection.Manifest.TotalWeight, 9);
    }

    [Fact]
    public void ASelection_IsRepeatable_WhateverOrderTheLabelsCameIn()
    {
        var labels = Many("a", 300).Concat(Many("b", 60)).ToList();
        var small = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxLabels = 100 });
        var again = TrainingSetSelector.Select(Enumerable.Reverse(labels), new TrainingSetPolicy { MaxLabels = 100 });

        Assert.Equal(small.Labels.Select(l => l.DecisionId), again.Labels.Select(l => l.DecisionId));
        Assert.Equal(small.Manifest.ContentSha256, again.Manifest.ContentSha256);
    }

    [Fact]
    public void ASliceCap_LimitsABigSlice_AndLeavesARareOneWhole()
    {
        var labels = Many("big", 900).Concat(Many("rare", 20)).ToList();
        var selection = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxPerSlice = 100 });

        Assert.Equal(new Dictionary<string, int> { ["big"] = 100, ["rare"] = 20 }, selection.Manifest.BySlice);
        Assert.Equal(800, selection.Manifest.Dropped!["slice"]);
    }

    [Fact]
    public void ABudget_SharesOutInProportion_AfterTheFloors_AndHitsTheBudgetExactly()
    {
        var labels = Many("a", 800).Concat(Many("b", 150)).Concat(Many("c", 50)).ToList();
        var selection = TrainingSetSelector.Select(labels, new TrainingSetPolicy
        {
            MaxLabels = 200,
            MinPerSlice = new Dictionary<string, int> { ["c"] = 40 },
        });

        Assert.Equal(200, selection.Labels.Count);
        Assert.True(selection.Manifest.BySlice["c"] >= 40);
        // 160 left after c's floor, shared by what a, b and c still have (800, 150, 10): 800/960 of 160 is 133.
        Assert.InRange(selection.Manifest.BySlice["a"], 130, 136);
        Assert.Equal(800, selection.Manifest.Dropped!["budget"]);

        // A budget the data fits in, or no budget, thins nothing.
        Assert.Equal(1000, TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxLabels = 5000 }).Labels.Count);
        Assert.Equal(1000, TrainingSetSelector.Select(labels, new TrainingSetPolicy()).Labels.Count);
        Assert.Empty(TrainingSetSelector.Select(labels, new TrainingSetPolicy()).Manifest.Dropped!);

        Assert.Throws<ArgumentException>(() => TrainingSetSelector.Select(labels, new TrainingSetPolicy
        {
            MaxLabels = 50,
            MinPerSlice = new Dictionary<string, int> { ["a"] = 40, ["b"] = 40 },
        }));
    }

    [Fact]
    public void Compensating_MakesTheLabelsKeptStandForThoseLeftOut()
    {
        var labels = Many("a", 400).Concat(Many("b", 100)).ToList();
        var plain = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxPerSlice = 50 });
        var compensated = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxPerSlice = 50, Compensate = true });

        Assert.Equal(100, plain.Manifest.TotalWeight, 9);                           // 50 + 50 labels, each its own weight
        Assert.Equal(labels.Sum(l => l.Weight), compensated.Manifest.TotalWeight, 9); // every offered label still counted
        Assert.All(compensated.Labels.Where(l => l.SliceKey == "a"), l => Assert.Equal(50.0 / 400, l.SelectionProbability, 12));
        Assert.True(compensated.Manifest.Compensated);
        Assert.False(plain.Manifest.Compensated);
        Assert.NotEqual(plain.Manifest.ContentSha256, compensated.Manifest.ContentSha256);
    }

    [Fact]
    public void TheLimitsCompose_AndTheKeepProbabilitiesMultiply()
    {
        var labels = Many("a", 200, i => $"u{i % 20}");                            // 20 units of ten
        var selection = TrainingSetSelector.Select(labels,
            new TrainingSetPolicy { MaxPerUnit = 5, MaxPerSlice = 50, Compensate = true });

        Assert.Equal(50, selection.Labels.Count);
        Assert.Equal(50, selection.Manifest.Labels);
        Assert.Equal(200, selection.Manifest.TotalWeight, 9);                       // 5/10 of each unit, then 50 of 100
        Assert.All(selection.Labels, l => Assert.Equal(0.25, l.SelectionProbability, 12));
        Assert.Equal(100, selection.Manifest.Dropped!["unit"]);
        Assert.Equal(50, selection.Manifest.Dropped!["slice"]);
    }

    [Fact]
    public void ASelection_RefusesAHeldOutLabel_AndALimitThatIsNotACount()
    {
        var held = Label("d1", "s") with { Share = LabelShare.Evaluation, ShareProbability = 0.2 };
        Assert.Throws<ArgumentException>(() => TrainingSetSelector.Select([held], new TrainingSetPolicy()));
        Assert.Throws<ArgumentException>(() => TrainingSetSelector.Select([Label("d1", "s")], new TrainingSetPolicy { MaxLabels = 0 }));
        Assert.Throws<ArgumentException>(() => TrainingSetSelector.Select([Label("d1", "s")], new TrainingSetPolicy { MaxPerUnit = -1 }));
    }

    [Fact]
    public void AUnitOfTheCallersOwn_StandsInForTheSplitKey()
    {
        var labels = Many("s", 60);
        var selection = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxPerUnit = 1 }, unitOf: l => "u" + (int.Parse(l.DecisionId.Split('-')[1]) % 6));

        Assert.Equal(6, selection.Labels.Count);
    }
    // A tenth of the labels are worth ten times the others.
    private static double Tenfold(ConfirmedLabel<string> l) => l.DecisionId.EndsWith('7') ? 10.0 : 1.0;

    [Fact]
    public void APriority_TiltsAThinnedSetTowardTheLabelsItRanksFirst_AtTheSameSize()
    {
        var labels = Many("s", 1000);
        var even = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxLabels = 100 });
        var tilted = TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxLabels = 100 }, priority: Tenfold);

        Assert.Equal(100, tilted.Labels.Count);                                    // a fixed-size draw
        Assert.True(tilted.Manifest.Prioritized);
        Assert.False(even.Manifest.Prioritized ?? false);
        var favored = tilted.Labels.Count(l => Tenfold(l) > 1);
        Assert.InRange(favored, 35, 100);                                          // ≈ 40 of 100 vs ≈ 10 by chance
        Assert.True(favored > even.Labels.Count(l => Tenfold(l) > 1));
        Assert.Equal(900, tilted.Manifest.Dropped!["budget"]);
    }

    [Fact]
    public void APriority_WithCompensate_KeepsEveryLabelsOwnChance_AndStandsForTheWholeOnAverage()
    {
        var labels = Many("s", 400);
        var totals = Enumerable.Range(0, 200).Select(seed =>
        {
            var set = TrainingSetSelector.Select(labels,
                new TrainingSetPolicy { MaxLabels = 80, Compensate = true, Salt = $"salt-{seed}" }, priority: Tenfold);
            Assert.All(set.Labels, l => Assert.InRange(l.SelectionProbability, 1e-6, 1.0));
            return set.Labels.Sum(l => 1 / l.SelectionProbability);                 // Horvitz-Thompson count of the offered labels
        }).ToList();

        Assert.Equal(400, totals.Average(), 400 * 0.03);                           // unbiased within sampling noise
    }

    [Fact]
    public void APrioritizedSelection_IsRepeatable_WhateverOrderTheLabelsCameIn()
    {
        var labels = Many("a", 300).Concat(Many("b", 60)).ToList();
        var policy = new TrainingSetPolicy { MaxLabels = 100, Compensate = true };
        var once = TrainingSetSelector.Select(labels, policy, priority: Tenfold);
        var again = TrainingSetSelector.Select(Enumerable.Reverse(labels), policy, priority: Tenfold);

        Assert.Equal(once.Labels.Select(l => l.DecisionId), again.Labels.Select(l => l.DecisionId));
        Assert.Equal(once.Manifest.ContentSha256, again.Manifest.ContentSha256);
    }

    [Fact]
    public void InclusionProbabilities_SumToTheSize_StayInZeroToOne_AndHoldTheOverwhelmingOnesAtOne()
    {
        // Two labels dominate: their even-proportion chance would exceed 1 for a set of 5.
        double[] priorities = [1000, 1000, 1, 1, 1, 1, 1, 1, 1, 1];
        var p = TrainingSetSelector.InclusionProbabilities(priorities, 5, 0.1);

        Assert.Equal(5.0, p.Sum(), 9);
        Assert.All(p, x => Assert.InRange(x, 1e-9, 1.0));
        Assert.Equal(1.0, p[0], 9);
        Assert.Equal(1.0, p[1], 9);
        Assert.True(p[2] > 0.05);                                                  // the uniform mix keeps the rest alive
    }

    [Fact]
    public void ASystematicDraw_PicksExactlyTheSize_WhateverTheStart()
    {
        double[] inclusion = [0.3, 0.9, 0.5, 0.6, 0.7, 1.0, 0.25, 0.75];            // sums to 5
        foreach (var start in new[] { 0.0, 0.2, 0.5, 0.999 })
            Assert.Equal(5, TrainingSetSelector.SystematicDraw(inclusion, start).Count(x => x > 0));
    }

    [Fact]
    public void ABadPriority_OrMix_IsRefused()
    {
        var labels = Many("s", 20);
        Assert.Throws<ArgumentException>(() =>
            TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxLabels = 5 }, priority: _ => -1));
        Assert.Throws<ArgumentException>(() =>
            TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxLabels = 5 }, priority: _ => double.NaN));
        Assert.Throws<ArgumentException>(() =>
            TrainingSetSelector.Select(labels, new TrainingSetPolicy { MaxLabels = 5, PriorityUniformMix = 1.5 }, priority: _ => 1));
    }

    [Fact]
    public void PriorityFrom_ReadsTheRechecksSelection_AndTreatsAnUnscoredLabelAsOrdinary()
    {
        var selection = new KnobSelection
        {
            Shape = SelectionShape.WeightedRisk,
            Calibration = [new RiskBin(0.5, 0.1), new RiskBin(1.0, 0.7)],
            MeanScore = 0.4,
            UniformMix = 0.2,
        };
        var risks = new Dictionary<string, double> { ["low"] = 0.2, ["high"] = 0.9 };
        var priority = TrainingSetSelector.PriorityFrom<string>(selection,
            l => risks.TryGetValue(l.DecisionId, out var r) ? new SelectionSignal { Risk = r } : null);

        Assert.Equal(0.4, priority(Label("low", "s")), 9);                         // 0.8 · 0.1 / 0.4 + 0.2
        Assert.Equal(1.6, priority(Label("high", "s")), 9);                        // 0.8 · 0.7 / 0.4 + 0.2
        Assert.Equal(1.0, priority(Label("no-signal", "s")), 9);
        Assert.Equal(1.0, TrainingSetSelector.PriorityFrom<string>(selection with { Calibration = [] },
            _ => new SelectionSignal { Risk = 0.9 })(Label("high", "s")), 9);       // not calibrated: nothing to tilt by
    }
}
