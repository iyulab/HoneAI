using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class MetaMaskMeasureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tier Llm = new() { Id = "llm", Kind = JudgeKind.Llm, Version = "p2", CostRank = 2 };
    private static readonly Tier Other = new() { Id = "other", Kind = JudgeKind.Llm, Version = "p1", CostRank = 2 };

    private static RecheckResult<string> Said(string decisionId, string? value, Tier? tier = null, bool shown = false)
        => new(decisionId, tier ?? Llm, value is null ? JudgeVerdict<string>.Abstain("no answer") : JudgeVerdict<string>.Answer(value), Now) { Shown = shown };

    private static MaskedPairRecord Pair(string id, string meta, string? original, string? masked, Tier? tier = null)
        => MetaMaskMeasure.Pair(Said(id, original, tier), Said(id, masked, tier), "s", "source", meta);

    // `n` decisions of source `meta` the teacher called `from` and, with the source masked, `to`.
    private static IEnumerable<MaskedPairRecord> Moves(string meta, string from, string to, int n, string prefix, Tier? tier = null)
        => Enumerable.Range(0, n).Select(i => Pair($"{prefix}{i}", meta, from, to, tier));

    [Fact]
    public void ALopsidedMove_ForOneSource_IsFlagged_AndABalancedOneIsNot()
    {
        var pairs = Moves("X", "danger", "ok", 15, "x")                                   // masked, X's calls drift off «danger»
            .Concat(Moves("Y", "danger", "ok", 8, "ya")).Concat(Moves("Y", "ok", "danger", 8, "yb"))   // Y: noise both ways
            .Concat(Moves("Y", "ok", "ok", 30, "yc"))                                    // steady, no discordance
            .ToList();

        var report = MetaMaskMeasure.Compute(pairs, "llm");

        var x = report.Cells.Single(c => c is { MetaValue: "X", Value: "danger" });
        Assert.Equal((15, 15, 0), (x.Pairs, x.LeftValue, x.JoinedValue));
        Assert.Equal(2 * Math.Pow(0.5, 15), x.P!.Value, 12);
        Assert.Equal(4, report.Tests);                                                    // X × {danger, ok}, Y × {danger, ok}
        Assert.Equal(Math.Min(1, x.P!.Value * 4), x.AdjustedP!.Value, 12);
        Assert.True(x.Flagged);
        Assert.Equal(["X", "X"], report.Flagged.Select(c => c.MetaValue));                // «danger» and its mirror «ok»
        Assert.All(report.Cells.Where(c => c.MetaValue == "Y"), c => Assert.False(c.Flagged));
        Assert.Equal(1.0, report.Cells.Single(c => c is { MetaValue: "Y", Value: "danger" }).P);
    }

    [Fact]
    public void ThePairsOfOtherTeachers_AndUnansweredOnes_AreLeftOut_AndNoDiscordanceHasNoP()
    {
        var pairs = Moves("X", "danger", "ok", 15, "x").Concat(Moves("X", "danger", "ok", 15, "o", Other)).ToList();
        pairs.Add(Pair("u1", "X", "danger", null));                                       // the masked judgment gave no answer
        pairs.Add(Pair("u2", "X", null, "ok"));

        var report = MetaMaskMeasure.Compute(pairs, "llm");
        Assert.Equal(15, report.Cells.Single(c => c is { MetaValue: "X", Value: "danger" }).Pairs);

        var steady = MetaMaskMeasure.Compute(Moves("X", "ok", "ok", 20, "s"), "llm");
        var cell = Assert.Single(steady.Cells);
        Assert.Null(cell.P);
        Assert.Null(cell.AdjustedP);
        Assert.False(cell.Flagged);
        Assert.Empty(MetaMaskMeasure.Compute([], "llm").Cells);
    }

    [Fact]
    public void ThePairs_AreOneDecisionOfOneTeacher_JudgedBlindTwice_AndTheLevelIsAProbability()
    {
        Assert.Throws<ArgumentException>(() => MetaMaskMeasure.Pair(Said("d1", "ok"), Said("d2", "ok"), "s", "source", "X"));
        Assert.Throws<ArgumentException>(() => MetaMaskMeasure.Pair(Said("d1", "ok"), Said("d1", "ok", Other), "s", "source", "X"));
        Assert.Throws<ArgumentException>(() => MetaMaskMeasure.Pair(Said("d1", "ok", shown: true), Said("d1", "ok"), "s", "source", "X"));
        Assert.Throws<ArgumentException>(() => MetaMaskMeasure.Pair(Said("d1", "ok"), Said("d1", "ok"), "s", " ", "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetaMaskMeasure.Compute([], "llm", level: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetaMaskMeasure.Compute([], "llm", level: 1));

        var pair = Pair("d1", "X", "danger", "ok");
        Assert.Equal(("danger", "ok", "source", "X"), (pair.Original, pair.Masked, pair.MetaKey, pair.MetaValue));
    }
}
