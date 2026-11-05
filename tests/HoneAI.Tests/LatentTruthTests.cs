#pragma warning disable HONEAI001 // the experimental latent-truth surface is what is under test
using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class LatentTruthTests
{
    private static readonly string[] Classes = ["ok", "danger"];

    // 300 items, a quarter dangerous. Three judges: accurate (0.95), fair (0.8), and one that waves danger
    // through as ok two times in three while never raising a false alarm (a lean toward PASS).
    private static (List<JudgeLabel<string>> Labels, Dictionary<string, string> Truth) World(int judges = 3, int seed = 7)
    {
        var random = new Random(seed);
        var labels = new List<JudgeLabel<string>>();
        var truth = new Dictionary<string, string>();
        for (var i = 0; i < 300; i++)
        {
            var id = $"d{i}";
            var value = random.NextDouble() < 0.25 ? "danger" : "ok";
            truth[id] = value;
            string Flip(string v) => v == "ok" ? "danger" : "ok";
            labels.Add(new(id, new("ml"), random.NextDouble() < 0.95 ? value : Flip(value)));
            labels.Add(new(id, new("llm"), random.NextDouble() < 0.8 ? value : Flip(value)));
            if (judges >= 3)
                labels.Add(new(id, new("lenient"), value == "danger" && random.NextDouble() < 2.0 / 3 ? "ok" : value));
        }
        return (labels, truth);
    }

    [Fact]
    public void ThreeJudges_RecoverEachJudgesConfusion_AndTheLean()
    {
        var (labels, truth) = World();

        var estimate = LatentTruth.Estimate(labels, Classes);

        Assert.True(estimate.Converged);
        var judge = estimate.Judges.ToDictionary(j => j.Judge.TierId);
        Assert.InRange(judge["ml"].Accuracy, 0.90, 0.99);
        Assert.InRange(judge["llm"].Accuracy, 0.73, 0.87);
        Assert.InRange(judge["lenient"].Confusion["danger"]["ok"], 0.55, 0.78);   // the lean, measured
        Assert.InRange(judge["lenient"].Confusion["ok"]["danger"], 0.0, 0.03);    // and no false alarms
        Assert.InRange(estimate.Prior["danger"], 0.18, 0.32);

        var right = estimate.Items.Count(item => item.MostLikely == truth[item.ItemId]);
        Assert.True(right >= 285, $"{right} of 300 recovered");
    }

    [Fact]
    public void WhenTheMajorityLeans_TheEstimateCatchesWhatAVoteWavesThrough()
    {
        // One fair judge (0.85 both ways) and two that wave danger through two times in three but never raise a false
        // alarm. A vote follows the lenient pair; the estimate learns that their «danger» is never wrong.
        var random = new Random(11);
        var labels = new List<JudgeLabel<string>>();
        var truth = new Dictionary<string, string>();
        for (var i = 0; i < 400; i++)
        {
            var id = $"d{i}";
            var value = random.NextDouble() < 0.25 ? "danger" : "ok";
            truth[id] = value;
            labels.Add(new(id, new("fair"), random.NextDouble() < 0.85 ? value : value == "ok" ? "danger" : "ok"));
            foreach (var lenient in new[] { "lenient-a", "lenient-b" })
                labels.Add(new(id, new(lenient), value == "danger" && random.NextDouble() < 2.0 / 3 ? "ok" : value));
        }
        var dangers = truth.Where(kv => kv.Value == "danger").Select(kv => kv.Key).ToHashSet();

        var estimate = LatentTruth.Estimate(labels, Classes);
        var caughtByEstimate = estimate.Items.Count(item => dangers.Contains(item.ItemId) && item.MostLikely == "danger");
        var caughtByVote = labels.Where(l => dangers.Contains(l.ItemId)).GroupBy(l => l.ItemId)
            .Count(g => g.Count(l => l.Value == "danger") >= 2);

        Assert.True(caughtByEstimate > caughtByVote + dangers.Count / 10,
            $"estimate caught {caughtByEstimate}, vote {caughtByVote}, of {dangers.Count}");
    }

    [Fact]
    public void TwoJudges_AreNotIdentified_UnlessSomeTruthIsKnown()
    {
        var (labels, truth) = World(judges: 2);

        var error = Assert.Throws<InvalidOperationException>(() => LatentTruth.Estimate(labels, Classes));
        Assert.Contains("not identified", error.Message);

        var planted = truth.Take(40).ToDictionary(kv => kv.Key, kv => kv.Value);
        var estimate = LatentTruth.Estimate(labels, Classes, planted);

        var item = estimate.Items.First(i => i.ItemId == planted.Keys.First());
        Assert.True(item.Known);
        Assert.Equal(1.0, item.Posterior[planted[item.ItemId]]);
        Assert.InRange(estimate.Judges.Single(j => j.Judge.TierId == "ml").Accuracy, 0.90, 0.99);
    }

    [Fact]
    public void PeopleOnOneRung_AreSeparateJudges()
    {
        var labels = new List<JudgeLabel<string>>();
        for (var i = 0; i < 60; i++)
        {
            var value = i % 4 == 0 ? "danger" : "ok";
            labels.Add(new($"d{i}", new("people", "p-1"), value));
            labels.Add(new($"d{i}", new("people", "p-2"), value));
            labels.Add(new($"d{i}", new("people", "p-3"), i % 4 == 0 && i % 8 == 0 ? "ok" : value));   // misses half
        }

        var estimate = LatentTruth.Estimate(labels, Classes);

        Assert.Equal(3, estimate.Judges.Count);
        var p3 = estimate.Judges.Single(j => j.Judge.JudgedBy == "p-3");
        Assert.InRange(p3.Confusion["danger"]["ok"], 0.4, 0.6);
    }

    [Fact]
    public void WhatCannotBeEstimated_IsRefused()
    {
        var a = new JudgeIdentity("a");
        Assert.Throws<ArgumentException>(() => LatentTruth.Estimate([new JudgeLabel<string>("d1", a, "maybe")], Classes));
        Assert.Throws<ArgumentException>(() => LatentTruth.Estimate([new JudgeLabel<string>("d1", a, "ok")], ["ok"]));
        Assert.Throws<ArgumentException>(() => LatentTruth.Estimate(
            [new JudgeLabel<string>("d1", a, "ok"), new JudgeLabel<string>("d1", a, "danger")], Classes,
            new Dictionary<string, string> { ["d1"] = "ok" }));
    }

    // 3,000 items graded 0–4. Three dense judges that answer every item, lean one grade down and copy the first one's
    // answer half the time; four sparse judges (300 items each, 15 of them from a pool of 200 items of known grade).
    // Fixed seed: the estimate needs 287 rounds here — more than the 200 that used to be the default.
    private static (List<JudgeLabel<int>> Labels, Dictionary<string, int> Known) SlowTailWorld(
        int seed = 4, int sparse = 300, int planted = 15)
    {
        const int items = 3000, grades = 5;
        var random = new Random(seed);
        int Clamp(int g) => Math.Max(0, Math.Min(grades - 1, g));
        var truth = Enumerable.Range(0, items).Select(_ => random.Next(grades)).ToArray();
        var labels = new List<JudgeLabel<int>>();
        var dense = Enumerable.Range(0, 3).Select(i => new JudgeIdentity("teacher", $"t{i}")).ToArray();
        var first = new int[items];
        for (var i = 0; i < items; i++)
            for (var t = 0; t < 3; t++)
            {
                var answer = t > 0 && random.NextDouble() < 0.5
                    ? first[i]
                    : random.NextDouble() < 0.15 ? random.Next(grades) : Clamp(truth[i] - 1);
                if (t == 0) first[i] = answer;
                labels.Add(new($"i{i}", dense[t], answer));
            }
        foreach (var (name, accuracy, lean) in new[] { ("good", 0.95, 0), ("avg", 0.85, 0), ("erratic", 0.70, 0), ("leaning", 0.95, -1) })
        {
            var judge = new JudgeIdentity("human", name);
            var mine = Enumerable.Range(0, 200).OrderBy(_ => random.Next()).Take(planted)
                .Concat(Enumerable.Range(200, items - 200).OrderBy(_ => random.Next()).Take(sparse - planted))
                .ToList();   // drawn now, so the order of random numbers does not depend on when it is enumerated
            foreach (var i in mine)
                labels.Add(new($"i{i}", judge, random.NextDouble() < accuracy ? Clamp(truth[i] + lean) : random.Next(grades)));
        }
        return (labels, Enumerable.Range(0, 200).ToDictionary(i => $"i{i}", i => truth[i]));
    }

    [Fact]
    public void ASlowTail_IsNamedForWhatItIs_NotJustFlaggedUnconverged()
    {
        var (labels, known) = SlowTailWorld();
        var classes = Enumerable.Range(0, 5).ToList();
        var options = new LatentTruthOptions();

        var cutOff = LatentTruth.Estimate(labels, classes, known, options with { MaxIterations = 200 });
        var complete = LatentTruth.Estimate(labels, classes, known);

        Assert.False(cutOff.Converged);
        Assert.True(cutOff.FinalMovement > options.Tolerance, $"{cutOff.FinalMovement}");
        Assert.True(complete.Converged);
        Assert.True(complete.FinalMovement <= options.Tolerance, $"{complete.FinalMovement}");
        Assert.InRange(complete.Iterations, 201, options.MaxIterations);

        // The judges' figures had already settled when the short run stopped.
        foreach (var judge in complete.Judges)
        {
            var early = cutOff.Judges.Single(j => j.Judge == judge.Judge);
            Assert.InRange(Math.Abs(early.Accuracy - judge.Accuracy), 0, 0.01);
        }
    }

    [Fact]
    public void KnownItems_PullTheEstimateOutOfADenseBlockThatSharesALean()
    {
        // Three judges answer every item and lean one grade down together; four others answer 100 items each, 10 of them
        // from a pool of 200 known ones, and one of the four leans the same way. Read from the vote share the dense
        // block begins as the truth and the leaning judge, which agrees with the block, mostly does not come out worst;
        // read through the known items first it does, which it is.
        var classes = Enumerable.Range(0, 5).ToList();
        var worst = 0;
        for (var seed = 1; seed <= 10; seed++)
        {
            var (labels, known) = SlowTailWorld(seed, sparse: 100, planted: 10);
            var estimate = LatentTruth.Estimate(labels, classes, known);
            var humans = estimate.Judges.Where(j => j.Judge.TierId == "human").OrderBy(j => j.Accuracy).ToList();
            if (humans[0].Judge.JudgedBy == "leaning")
                worst++;
        }
        Assert.True(worst >= 9, $"the leaning judge came out worst in {worst} of 10 runs");
    }

    [Fact]
    public void AKnownItemNobodyAnswered_LeavesTheStartAsItWas()
    {
        var (labels, truth) = World();

        var estimate = LatentTruth.Estimate(labels, Classes, new Dictionary<string, string> { ["unseen"] = "ok" });

        Assert.True(estimate.Converged);
        var right = estimate.Items.Where(i => i.ItemId != "unseen").Count(item => item.MostLikely == truth[item.ItemId]);
        Assert.True(right >= 285, $"{right} of 300 recovered");
        Assert.True(estimate.Items.Single(i => i.ItemId == "unseen").Known);
    }

    [Fact]
    public void WhenEveryItemIsKnown_NothingMoves()
    {
        var a = new JudgeIdentity("a");
        var known = new Dictionary<string, string> { ["d1"] = "ok", ["d2"] = "danger" };

        var estimate = LatentTruth.Estimate(
            [new("d1", a, "ok"), new("d2", a, "danger")], Classes, known);

        Assert.True(estimate.Converged);
        Assert.Equal(0, estimate.FinalMovement);
    }

    [Fact]
    public void TheSurface_IsMarkedExperimental()
    {
        var attribute = typeof(LatentTruth).GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.ExperimentalAttribute), false)
            .Cast<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>().Single();
        Assert.Equal(LatentTruth.DiagnosticId, attribute.DiagnosticId);
    }
}
