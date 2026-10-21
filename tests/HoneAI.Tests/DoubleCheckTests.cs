using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class DoubleCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
    private static readonly Tier Llm = new() { Id = "llm", Kind = JudgeKind.Llm, Version = "p2", CostRank = 2 };
    private static readonly Tier People = new() { Id = "people", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };

    private static readonly ISettlementPolicy<string> Policy =
        SettlementPolicy.Categorical(settleable: ["ok", "minor"], mustCatch: ["danger"]);

    private static RecheckResult<string> Judged(Tier tier, string decision, string? value, string? who = null, bool shown = false)
        => new(decision, tier,
            value is null ? JudgeVerdict<string>.Abstain("unsure", judgedBy: who) : JudgeVerdict<string>.Answer(value, judgedBy: who),
            Now)
        { Shown = shown };

    [Fact]
    public void APair_IsUnordered_AndScoredAgainstTheKnownAnswer()
    {
        var one = DoubleCheckMeasure.Pair(Judged(Ml, "d1", "ok"), Judged(Llm, "d1", "danger"), "s", Policy, new Known<string>("danger"));
        var other = DoubleCheckMeasure.Pair(Judged(Llm, "d1", "danger"), Judged(Ml, "d1", "ok"), "s", Policy, new Known<string>("danger"));

        Assert.Equal(one, other);
        Assert.Equal(new JudgeIdentity("llm"), one.First);            // ordinal: "llm" < "ml"
        Assert.Equal((false, true, false), (one.Agreed!.Value, one.FirstMustCatch!.Value, one.SecondMustCatch!.Value));
        Assert.Equal((true, false, true), (one.FirstCorrect!.Value, one.SecondCorrect!.Value, one.TruthMustCatch!.Value));
    }

    [Fact]
    public void WhatIsNotADoubleCheck_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => DoubleCheckMeasure.Pair(Judged(Ml, "d1", "ok"), Judged(Llm, "d2", "ok"), "s", Policy));
        Assert.Throws<ArgumentException>(() => DoubleCheckMeasure.Pair(Judged(Ml, "d1", "ok"), Judged(Ml, "d1", "ok"), "s", Policy));
        Assert.Throws<ArgumentException>(
            () => DoubleCheckMeasure.Pair(Judged(Ml, "d1", "ok"), Judged(Llm, "d1", "ok", shown: true), "s", Policy));
    }

    [Fact]
    public void TwoPeopleOnOneRung_AreTwoJudges_AndTheirSpreadIsKappa()
    {
        // Twenty items: p-1 calls danger on 1–10, p-2 on 1–8 and 11–12 — 16 of 20 agree; each calls half.
        var records = Enumerable.Range(1, 20).Select(i => DoubleCheckMeasure.Pair(
            Judged(People, $"d{i}", i <= 10 ? "danger" : "ok", "p-1"),
            Judged(People, $"d{i}", i <= 8 || i is 11 or 12 ? "danger" : "ok", "p-2"),
            "s", Policy)).ToList();

        var cell = DoubleCheckMeasure.Compute(records, new JudgeIdentity("people", "p-2"), new JudgeIdentity("people", "p-1"));

        Assert.True(cell.SameKind);
        Assert.Equal(20, cell.Pairs);
        Assert.Equal(0.8, cell.Agreement!.Value, 12);
        Assert.Equal(0.6, cell.MustCatchKappa!.Value, 12);           // (0.8 − 0.5) / (1 − 0.5)
        Assert.Equal(0, cell.KnownPairs);
        Assert.Null(cell.ErrorOverlap);
    }

    [Fact]
    public void ErrorsThatCoincide_AreShownAsOverlap_IndependentOnesAreNot()
    {
        // Known answers, 20 items, each judge wrong on 4. Coinciding: the same 4. Independent-looking: 1 shared.
        static List<DoubleCheckRecord> Records(Func<int, bool> mlWrong, Func<int, bool> llmWrong)
            => Enumerable.Range(1, 20).Select(i => DoubleCheckMeasure.Pair(
                Judged(Ml, $"d{i}", mlWrong(i) ? "ok" : "danger"),
                Judged(Llm, $"d{i}", llmWrong(i) ? "ok" : "danger"),
                "s", Policy, new Known<string>("danger"))).ToList();

        var together = DoubleCheckMeasure.Compute(Records(i => i <= 4, i => i <= 4), new("ml"), new("llm"));
        var apart = DoubleCheckMeasure.Compute(Records(i => i <= 4, i => i is 4 or 5 or 6 or 7), new("ml"), new("llm"));

        Assert.False(together.SameKind);
        Assert.Equal((0.2, 0.2, 4), (together.FirstErrorRate!.Value, together.SecondErrorRate!.Value, together.BothWrong));
        Assert.Equal(5.0, together.ErrorOverlap!.Value, 12);        // 0.2 / (0.2 × 0.2)
        Assert.True(together.ErrorOverlapP < 0.001);
        Assert.Equal((20, 4), (together.MustCatchKnown, together.BothMissed));

        Assert.Equal(1.25, apart.ErrorOverlap!.Value, 12);          // 0.05 / 0.04
        Assert.True(apart.ErrorOverlapP > 0.3);
        Assert.Equal(1, apart.BothMissed);
    }

    [Fact]
    public void AJudgeThatGaveNoAnswer_LeavesThePairOutOfTheCounts()
    {
        var records = new[]
        {
            DoubleCheckMeasure.Pair(Judged(Ml, "d1", "ok"), Judged(Llm, "d1", null), "s", Policy, new Known<string>("ok")),
            DoubleCheckMeasure.Pair(Judged(Ml, "d2", "ok"), Judged(Llm, "d2", "ok"), "s", Policy, new Known<string>("ok")),
        };

        var cell = DoubleCheckMeasure.Compute(records, new("ml"), new("llm"));

        Assert.Equal((1, 1), (cell.Pairs, cell.KnownPairs));
        Assert.Null(cell.MustCatchKappa);                           // both called «not must-catch» throughout
    }

    [Theory]
    [InlineData(4, 20, 4, 4, 1.0 / 4845)]                          // C(4,4)·C(16,0)/C(20,4)
    [InlineData(0, 20, 4, 4, 1.0)]
    public void HypergeometricUpperTail_MatchesTheExactValue(int k, int n, int successes, int draws, double expected)
        => Assert.Equal(expected, DoubleCheckMeasure.HypergeometricUpperTail(k, n, successes, draws), 9);
}
