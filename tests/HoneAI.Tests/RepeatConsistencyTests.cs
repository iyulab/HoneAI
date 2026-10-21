using System;
using System.Linq;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class RepeatConsistencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

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
    public void ErrorsSplit_IntoABiasTheRepeatKeeps_AndNoiseItExposes()
    {
        // Ten dangerous items asked twice. 1–3: ok both times (a lean toward PASS); 4–5: danger then ok; 6–10: danger.
        var records = Enumerable.Range(1, 10).Select(i => RepeatConsistency.Pair(
            Judged(Llm, $"d{i}", i <= 3 ? "ok" : "danger"),
            Judged(Llm, $"again-{i}", i <= 5 ? "ok" : "danger"),             // asked again under another id
            "s", Policy, new Known<string>("danger"))).ToList();

        var cell = RepeatConsistency.Compute(records, new JudgeIdentity("llm"));

        Assert.Equal(10, cell.Repeats);
        Assert.Equal(0.8, cell.Consistency!.Value, 12);
        Assert.Equal(2, cell.MustCatchFlips);
        Assert.Equal(10, cell.KnownRepeats);
        Assert.Equal(0.3, cell.SystematicErrorRate!.Value, 12);
        Assert.Equal(0.2, cell.NoiseRate!.Value, 12);
        Assert.Equal(5.0 / 8, cell.AccuracyWhenConsistent!.Value, 12);
        Assert.Equal("d4", records[3].DecisionId);
    }

    [Fact]
    public void APersonOnARungOfPeople_IsMeasuredOnTheirOwnRepeats()
    {
        var records = new[]
        {
            RepeatConsistency.Pair(Judged(People, "d1", "ok", "p-1"), Judged(People, "d1", "ok", "p-1"), "s", Policy),
            RepeatConsistency.Pair(Judged(People, "d2", "ok", "p-2"), Judged(People, "d2", "minor", "p-2"), "s", Policy),
        };

        Assert.Equal(1.0, RepeatConsistency.Compute(records, new("people", "p-1")).Consistency);
        Assert.Equal(0.0, RepeatConsistency.Compute(records, new("people", "p-2")).Consistency);
        Assert.Null(RepeatConsistency.Compute(records, new("people", "p-1")).NoiseRate);   // no known answer
    }

    [Fact]
    public void WhatIsNotARepeat_IsRefused_AndAnAbstentionIsLeftOut()
    {
        Assert.Throws<ArgumentException>(
            () => RepeatConsistency.Pair(Judged(People, "d1", "ok", "p-1"), Judged(People, "d1", "ok", "p-2"), "s", Policy));
        Assert.Throws<ArgumentException>(
            () => RepeatConsistency.Pair(Judged(Llm, "d1", "ok"), Judged(Llm, "d1", "ok", shown: true), "s", Policy));

        var abstained = RepeatConsistency.Pair(Judged(Llm, "d1", "ok"), Judged(Llm, "d1", null), "s", Policy);
        Assert.Null(abstained.Consistent);
        Assert.Equal(0, RepeatConsistency.Compute([abstained], new("llm")).Repeats);
    }
}
