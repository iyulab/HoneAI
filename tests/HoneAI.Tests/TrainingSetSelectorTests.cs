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
}
