using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class TeacherLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static PlantRecord Plant(string id, bool truthMustCatch, PlantOutcome outcome, bool answeredMustCatch,
        string slice = "s", int minutesAgo = 10, string tier = "teacher", string? judgedBy = null)
        => new()
        {
            PlantId = id,
            TierId = tier,
            JudgedBy = judgedBy,
            SliceKey = slice,
            JudgedAt = Now.AddMinutes(-minutesAgo),
            TruthMustCatch = truthMustCatch,
            Outcome = outcome,
            AnsweredMustCatch = answeredMustCatch,
        };

    [Theory]
    [InlineData(0, 10, 0.0)]
    [InlineData(10, 10, 0.7411344)]   // α^(1/n)
    [InlineData(18, 20, 0.7174)]      // exact one-sided 95% lower bound for 18/20
    public void ClopperPearsonLower_MatchesTheExactBound(int k, int n, double expected)
        => Assert.Equal(expected, TeacherLedger.ClopperPearsonLower(k, n, 0.05), 3);

    [Fact]
    public void TheCell_CountsMustCatchRecall_WithFailuresAsMissesAtTheLowerEnd()
    {
        var records = new List<PlantRecord>();
        for (var i = 0; i < 36; i++) records.Add(Plant($"c{i}", true, PlantOutcome.Correct, true));
        records.Add(Plant("m1", true, PlantOutcome.Wrong, false));                 // waved through
        records.Add(Plant("m2", true, PlantOutcome.Wrong, true));                  // another must-catch value: still caught
        records.Add(Plant("f1", true, PlantOutcome.Failed, false));
        records.Add(Plant("f2", true, PlantOutcome.Failed, false));
        for (var i = 0; i < 55; i++) records.Add(Plant($"o{i}", false, PlantOutcome.Correct, false));
        for (var i = 0; i < 3; i++) records.Add(Plant($"w{i}", false, PlantOutcome.Wrong, true));
        records.Add(Plant("f3", false, PlantOutcome.Failed, false));

        var cell = TeacherLedger.Compute(records, "teacher", Now.AddHours(-1), Now);

        Assert.Equal(99, cell.Plants);
        Assert.Equal(3, cell.Failures);
        Assert.Equal(40, cell.MustCatchPlants);
        Assert.Equal(37, cell.MustCatchCaught);
        Assert.Equal(2, cell.MustCatchFailures);
        Assert.Equal(37.0 / 38, cell.Recall!.Value, 12);
        Assert.Equal(TeacherLedger.ClopperPearsonLower(37, 40, 0.05), cell.RecallLower!.Value, 12);
        Assert.Equal(91.0 / 96, cell.Accuracy!.Value, 12);
        Assert.Equal(TeacherLedger.ClopperPearsonLower(91, 99, 0.05), cell.AccuracyLower!.Value, 12);
    }

    [Fact]
    public void ATeacherQualifies_OnlyWhenItsRecallLowerBoundClearsTheFloor()
    {
        var sharp = Enumerable.Range(0, 60).Select(i => Plant($"p{i}", true, PlantOutcome.Correct, true)).ToList();
        var few = sharp.Take(5).ToList();
        var none = Enumerable.Range(0, 60).Select(i => Plant($"q{i}", false, PlantOutcome.Correct, false)).ToList();

        Assert.True(TeacherLedger.Qualifies(TeacherLedger.Compute(sharp, "teacher", Now.AddHours(-1), Now), 0.9));
        // Five out of five is not enough to show 0.9: the bound is 0.05^(1/5) ≈ 0.55.
        Assert.False(TeacherLedger.Qualifies(TeacherLedger.Compute(few, "teacher", Now.AddHours(-1), Now), 0.9));
        Assert.False(TeacherLedger.Qualifies(TeacherLedger.Compute(none, "teacher", Now.AddHours(-1), Now), 0.5));
    }

    [Fact]
    public void TheCell_KeepsToItsTier_Window_AndSlice()
    {
        var records = new[]
        {
            Plant("a", true, PlantOutcome.Correct, true, slice: "s1"),
            Plant("b", true, PlantOutcome.Correct, true, slice: "s2"),
            Plant("c", true, PlantOutcome.Correct, true, minutesAgo: 120),
            Plant("d", true, PlantOutcome.Correct, true, tier: "other"),
        };

        Assert.Equal(2, TeacherLedger.Compute(records, "teacher", Now.AddHours(-1), Now).Plants);
        Assert.Equal(1, TeacherLedger.Compute(records, "teacher", Now.AddHours(-1), Now, sliceKey: "s1").Plants);
    }

    [Fact]
    public void OnARungOfPeople_EachPersonHasTheirOwnCell_AndTheRungPoolsThem()
    {
        var records = new[]
        {
            Plant("a", true, PlantOutcome.Correct, true, tier: "people", judgedBy: "p-1"),
            Plant("b", true, PlantOutcome.Correct, true, tier: "people", judgedBy: "p-1"),
            Plant("a", true, PlantOutcome.Wrong, false, tier: "people", judgedBy: "p-2"),   // the same plant, another person
        };

        var p1 = TeacherLedger.Compute(records, "people", Now.AddHours(-1), Now, judgedBy: "p-1");
        var p2 = TeacherLedger.Compute(records, "people", Now.AddHours(-1), Now, judgedBy: "p-2");
        var rung = TeacherLedger.Compute(records, "people", Now.AddHours(-1), Now);

        Assert.Equal(("p-1", 2, 1.0), (p1.JudgedBy, p1.Plants, p1.Recall!.Value));
        Assert.Equal(("p-2", 1, 0.0), (p2.JudgedBy, p2.Plants, p2.Recall!.Value));
        Assert.Equal((null, 3), (rung.JudgedBy, rung.Plants));
    }

    [Fact]
    public async Task APersonsAnswer_CarriesWhoJudged_OntoThePlantRecord()
    {
        var people = new Tier { Id = "people", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };
        var policy = SettlementPolicy.Categorical(new[] { "OK" }, new[] { "HIGH" });
        var router = new TieredRouter<string, string>(
            [Judge.From<string, string>(people, (_, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Answer("HIGH", judgedBy: "p-9")))],
            policy, SliceKey.From<string>("k", _ => "s"), id => id, () => null,
            new TieredRouterOptions { Clock = () => Now });

        var record = PlantRecords.From(await router.RecheckAsync("input", "plant-1", "people"), "HIGH", "s", policy);

        Assert.Equal("p-9", record.JudgedBy);
        Assert.Equal(PlantOutcome.Correct, record.Outcome);
    }

    [Fact]
    public async Task APlant_IsJudgedExactlyAsARealRecheck_AndRecordedAgainstItsKnownAnswer()
    {
        var ml = new Tier { Id = "ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
        var teacher = new Tier { Id = "teacher", Kind = JudgeKind.Llm, Version = "p-3", CostRank = 2, AcceptsHints = true };
        var seen = new List<JudgeContext>();
        var policy = SettlementPolicy.Categorical(new[] { "OK" }, new[] { "HIGH" });
        var router = new TieredRouter<string, string>(
            [Judge.Constant<string, string>(ml, "OK"),
             Judge.From<string, string>(teacher, (_, context, _) => { seen.Add(context); return ValueTask.FromResult(JudgeVerdict<string>.Answer("OK")); })],
            policy, SliceKey.From<string>("k", _ => "s"), id => id, () => null,
            new TieredRouterOptions { Clock = () => Now });

        var judgment = await router.RecheckAsync("input", "plant-7", "teacher");
        var record = PlantRecords.From(judgment, truth: "HIGH", sliceKey: "s", policy, truthSource: "consensus");

        var context = Assert.Single(seen);
        Assert.True(context.IsRecheck);
        Assert.Null(context.Hints);
        Assert.Equal("plant-7", record.PlantId);
        Assert.Equal("p-3", record.TierVersion);
        Assert.True(record.TruthMustCatch);
        Assert.Equal(PlantOutcome.Wrong, record.Outcome);
        Assert.False(record.AnsweredMustCatch);
        Assert.Equal("consensus", record.TruthSource);
    }

    [Fact]
    public async Task PlantStores_RecordOncePerPlantTierAndJudge_AndReadByJudgedTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"honeai-plants-{Guid.NewGuid():N}", "plants.jsonl");
        try
        {
            foreach (IPlantStore store in new IPlantStore[] { new InMemoryPlantStore(), new JsonlPlantStore(path) })
            {
                Assert.True(await store.AppendAsync(Plant("p1", true, PlantOutcome.Correct, true)));
                Assert.False(await store.AppendAsync(Plant("p1", true, PlantOutcome.Wrong, false)));   // redelivered
                Assert.True(await store.AppendAsync(Plant("p2", false, PlantOutcome.Correct, false, minutesAgo: 120)));
                Assert.True(await store.AppendAsync(Plant("p3", true, PlantOutcome.Correct, true, judgedBy: "x")));
                Assert.True(await store.AppendAsync(Plant("p3", true, PlantOutcome.Wrong, false, judgedBy: "y")));   // another person, same plant
                Assert.False(await store.AppendAsync(Plant("p3", true, PlantOutcome.Wrong, false, judgedBy: "y")));

                var inWindow = new List<PlantRecord>();
                await foreach (var r in store.ReadAsync("teacher", Now.AddHours(-1), Now))
                    inWindow.Add(r);

                Assert.Equal(["p1", "p3", "p3"], inWindow.Select(r => r.PlantId).Order());
                Assert.Equal(PlantOutcome.Correct, inWindow.Single(r => r.PlantId == "p1").Outcome);
            }
            // A second instance over the same file still sees what was written.
            Assert.False(await new JsonlPlantStore(path).AppendAsync(Plant("p1", true, PlantOutcome.Correct, true)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
