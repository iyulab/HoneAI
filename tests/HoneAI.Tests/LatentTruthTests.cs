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

    [Fact]
    public void TheSurface_IsMarkedExperimental()
    {
        var attribute = typeof(LatentTruth).GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.ExperimentalAttribute), false)
            .Cast<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>().Single();
        Assert.Equal(LatentTruth.DiagnosticId, attribute.DiagnosticId);
    }
}
