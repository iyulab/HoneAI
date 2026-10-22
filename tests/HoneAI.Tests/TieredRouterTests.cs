using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Routing;
using Xunit;

namespace HoneAI.Tests;

public class TieredRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static Tier T(string id, JudgeKind kind, int rank, bool hints = false)
        => new() { Id = id, Kind = kind, Version = id + "-v1", CostRank = rank, AcceptsHints = hints };

    private static readonly Tier Ml = T("edge-ml", JudgeKind.Ml, 1);
    private static readonly Tier Teacher = T("teacher", JudgeKind.Llm, 2);
    private static readonly Tier Person = T("reviewer", JudgeKind.Human, 3);

    // Inputs carry their slice and id; the policy settles OK and LOW, never HIGH.
    private sealed record Item(string Id, string Slice);

    private static readonly ISettlementPolicy<string> Policy =
        SettlementPolicy.Categorical(new[] { "OK", "LOW" }, new[] { "HIGH" });

    private static PolicySnapshot Snapshot(TrustState state, double rate = 0.2, string? sliceKeyVersion = null, DateTimeOffset? hard = null)
        => new()
        {
            Version = "snap-1",
            IssuedAt = Now.AddHours(-1),
            SoftExpiresAt = Now.AddHours(1),
            HardExpiresAt = hard ?? Now.AddHours(2),
            SliceKeyVersion = sliceKeyVersion,
            Entries = new[]
            {
                new PolicyEntry
                {
                    TierId = Ml.Id, SliceKey = "s1", State = state,
                    SampleRates = new Dictionary<string, double> { ["uniform"] = 0.05, ["slice-watch"] = rate },
                },
            },
        };

    private static TieredRouter<Item, string> Router(PolicySnapshot? snapshot, params IJudge<Item, string>[] judges)
        => new(judges, Policy, SliceKey.From<Item>("k1", i => i.Slice), i => i.Id, () => snapshot,
            new TieredRouterOptions { Clock = () => Now });

    private static IJudge<Item, string> Answers(Tier tier, string value, double? confidence = null)
        => Judge.Constant<Item, string>(tier, value, confidence);

    private static IJudge<Item, string> Abstains(Tier tier, string reason = "unsure", IReadOnlyDictionary<string, string>? hints = null)
        => Judge.From<Item, string>(tier, (_, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Abstain(reason, hints)));

    [Fact]
    public async Task WithoutASnapshot_TheLowerTierIsInShadow_AndTheTopSettles()
    {
        var router = Router(null, Answers(Ml, "OK", 0.99), Answers(Person, "LOW"));

        var decision = await router.DecideAsync(new Item("d1", "s1"));

        Assert.Equal("LOW", decision.Prediction.Value);
        var p = decision.Prediction.Provenance;
        Assert.Equal(JudgeKind.Human, p.Kind);
        Assert.Equal(new[] { StepOutcome.Shadowed, StepOutcome.Settled }, p.Path!.Select(s => s.Outcome));
        Assert.Null(p.Sample);                                    // the top is not rechecked
        Assert.Equal("OK", decision.Steps[0].Verdict.Value);      // what the shadow would have answered
    }

    [Fact]
    public async Task AnActiveTier_SettlesWhatItMaySettle_AndTheDrawIsRecorded()
    {
        var router = Router(Snapshot(TrustState.Active), Answers(Ml, "OK", 0.7), Answers(Person, "LOW"));

        var p = (await router.RouteAsync(new Item("d2", "s1"))).Provenance;

        Assert.Equal(JudgeKind.Ml, p.Kind);
        Assert.Equal("edge-ml", p.TierId);
        Assert.Equal("edge-ml-v1", p.TierVersion);
        Assert.Equal(TrustState.Active, p.TrustState);
        Assert.Equal("snap-1", p.SnapshotVersion);
        Assert.Equal("s1", p.SliceKey);
        Assert.Equal(0.7, p.Confidence);
        Assert.Equal((0.2, "slice-watch", RecheckDraw.IsDrawn("d2", "snap-1", 0.2)), (p.Sample!.Probability, p.Sample.Knob, p.Sample.Drawn));
        Assert.Equal(new Dictionary<string, double> { ["uniform"] = 0.05, ["slice-watch"] = 0.2 }, p.Sample.KnobRates);
        Assert.Single(p.Path!);
    }

    [Fact]
    public async Task ConfidenceIsNotALicence_AShadowTierDoesNotSettleEvenWhenCertain()
    {
        var router = Router(Snapshot(TrustState.Shadow), Answers(Ml, "OK", 1.0), Answers(Person, "OK"));

        var p = (await router.RouteAsync(new Item("d3", "s1"))).Provenance;

        Assert.Equal(JudgeKind.Human, p.Kind);
    }

    [Fact]
    public async Task AMustCatchValue_GoesToTheTop_WhateverTheTiersState()
    {
        var router = Router(Snapshot(TrustState.Stable), Answers(Ml, "HIGH"), Answers(Person, "HIGH"));

        var p = (await router.RouteAsync(new Item("d4", "s1"))).Provenance;

        Assert.Equal(JudgeKind.Human, p.Kind);
        Assert.Equal(StepOutcome.Escalated, p.Path![0].Outcome);
        Assert.Equal("must-catch value", p.Path[0].Detail);
    }

    [Fact]
    public async Task AnAnswerThePolicyDoesNotLetTheTierSettle_GoesUp()
    {
        var router = Router(Snapshot(TrustState.Active), Answers(Ml, "MAYBE"), Answers(Person, "OK"));

        var p = (await router.RouteAsync(new Item("d5", "s1"))).Provenance;

        Assert.Equal("not settleable by this tier", p.Path![0].Detail);
        Assert.Equal(JudgeKind.Human, p.Kind);
    }

    [Fact]
    public async Task AJudgeThatThrowsOrTimesOut_HasAbstained()
    {
        var throws = Judge.From<Item, string>(Ml, (_, _, _) => throw new InvalidOperationException("model not loaded"));
        var slow = Judge.From<Item, string>(Teacher, async (_, _, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return JudgeVerdict<string>.Answer("OK"); });
        var router = new TieredRouter<Item, string>(new[] { throws, slow, Answers(Person, "OK") }, Policy,
            SliceKey.Single<Item>(), i => i.Id, () => null,
            new TieredRouterOptions { Clock = () => Now, JudgeTimeout = TimeSpan.FromMilliseconds(50) });

        var p = (await router.RouteAsync(new Item("d6", "s1"))).Provenance;

        Assert.Equal("error: InvalidOperationException: model not loaded", p.Path![0].Detail);
        Assert.Equal("timeout", p.Path[1].Detail);
        Assert.Equal(StepOutcome.Settled, p.Path[2].Outcome);
    }

    [Fact]
    public async Task CallerCancellation_IsNotAnAbstention()
    {
        using var cts = new CancellationTokenSource();
        var cancels = Judge.From<Item, string>(Ml, (_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return default; });
        var router = Router(null, cancels, Answers(Person, "OK"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.RouteAsync(new Item("d7", "s1"), cts.Token));
    }

    [Fact]
    public async Task Hints_ReachOnlyATierThatAcceptsThem_AndTheProvenanceSaysSo()
    {
        JudgeContext? teacherSaw = null, personSaw = null;
        var teacher = Judge.From<Item, string>(T("teacher", JudgeKind.Llm, 2, hints: true),
            (_, c, _) => { teacherSaw = c; return ValueTask.FromResult(JudgeVerdict<string>.Abstain("unsure")); });
        var person = Judge.From<Item, string>(Person, (_, c, _) => { personSaw = c; return ValueTask.FromResult(JudgeVerdict<string>.Answer("LOW")); });
        var router = Router(null, Abstains(Ml, hints: new Dictionary<string, string> { ["path"] = "network/auth" }), teacher, person);

        var p = (await router.RouteAsync(new Item("d8", "s1"))).Provenance;

        Assert.Equal("network/auth", teacherSaw!.Hints!["path"]);
        Assert.Null(personSaw!.Hints);
        Assert.False(p.HadHint); // the person, who settled, had none
    }

    [Fact]
    public async Task AnUpperTierIsNotToldTheLowerAnswer()
    {
        JudgeContext? seen = null;
        var person = Judge.From<Item, string>(T("reviewer", JudgeKind.Human, 3, hints: true),
            (_, c, _) => { seen = c; return ValueTask.FromResult(JudgeVerdict<string>.Answer("OK")); });
        var router = Router(null, Answers(Ml, "LOW"), person);

        await router.RouteAsync(new Item("d9", "s1"));

        Assert.Null(seen!.Hints); // an answer is never a hint
    }

    [Fact]
    public async Task AfterHardExpiry_TheLowerTierGoesUp_AndIsUsedUnverifiedOnlyWhenNothingAboveAnswers()
    {
        var expired = Snapshot(TrustState.Stable, hard: Now.AddMinutes(-1));

        var reached = Router(expired, Answers(Ml, "OK"), Answers(Person, "LOW"));
        var p1 = (await reached.RouteAsync(new Item("d10", "s1"))).Provenance;
        Assert.Equal(JudgeKind.Human, p1.Kind);
        Assert.Equal("policy snapshot expired", p1.Path![0].Detail);
        Assert.False(p1.Unverified);

        var isolated = Router(expired, Answers(Ml, "OK"), Abstains(Person, "unreachable"));
        var p2 = (await isolated.RouteAsync(new Item("d10", "s1"))).Provenance;
        Assert.Equal(JudgeKind.Ml, p2.Kind);
        Assert.True(p2.Unverified);
        Assert.NotNull(p2.Sample); // still drawn, so the recheck can run when contact returns
    }

    [Fact]
    public async Task ASnapshotForAnotherSliceKeyDefinition_DoesNotApply()
    {
        var router = Router(Snapshot(TrustState.Stable, sliceKeyVersion: "k0"), Answers(Ml, "OK"), Answers(Person, "OK"));

        var p = (await router.RouteAsync(new Item("d11", "s1"))).Provenance;

        Assert.Equal(StepOutcome.Shadowed, p.Path![0].Outcome);
        Assert.Equal("snapshot slice-key version differs", p.Path[0].Detail);
    }

    [Fact]
    public async Task WhenNoTierSettles_TheRouterSaysSo_WithThePath()
    {
        var router = Router(null, Abstains(Ml), Abstains(Person, "no reviewer on shift"));

        var ex = await Assert.ThrowsAsync<DecisionNotSettledException>(() => router.RouteAsync(new Item("d12", "s1")));

        Assert.Equal("d12", ex.DecisionId);
        Assert.Equal("no reviewer on shift", ex.Path[1].Detail);
    }

    [Fact]
    public async Task ASnapshotWithoutRates_IsHeldToTheFloor()
    {
        var snapshot = Snapshot(TrustState.Stable) with
        {
            Entries = new[] { new PolicyEntry { TierId = Ml.Id, SliceKey = "s1", State = TrustState.Stable } },
        };
        var router = Router(snapshot, Answers(Ml, "OK"), Answers(Person, "OK"));

        var sample = (await router.RouteAsync(new Item("d13", "s1"))).Provenance.Sample!;

        Assert.Equal(0.01, sample.Probability);
        Assert.Equal("floor", sample.Knob);
    }

    [Fact]
    public void TheLadderHasOneOrder()
    {
        Assert.Throws<ArgumentException>(() => Router(null, Answers(Ml, "OK"), Answers(T("other", JudgeKind.Llm, 1), "OK")));
        Assert.Throws<ArgumentException>(() => Router(null, Answers(Ml, "OK"), Answers(T("edge-ml", JudgeKind.Llm, 2), "OK")));
    }

    [Fact]
    public void TiersAreOrderedByCost_NotByArgumentOrder()
    {
        var router = Router(null, Answers(Person, "OK"), Answers(Ml, "OK"));
        Assert.Equal(new[] { "edge-ml", "reviewer" }, router.Tiers.Select(t => t.Id));
    }

    [Fact]
    public async Task MemoryJudge_AnswersWhatItRemembers_AndAbstainsOtherwise()
    {
        var memory = new MemoryJudge<Item, string>(T("memory", JudgeKind.System1, 0), i => i.Slice);
        memory.Remember("s1", "OK");
        var context = new JudgeContext { DecisionId = "x", SliceKey = "s1" };

        Assert.Equal("OK", (await memory.JudgeAsync(new Item("x", "s1"), context)).Value);
        Assert.False((await memory.JudgeAsync(new Item("x", "s2"), context)).Answered);
    }
}

public class RecheckDrawTests
{
    [Fact]
    public void TheDrawIsFixedByTheDecisionAndTheSnapshot()
    {
        Assert.Equal(RecheckDraw.Uniform("d-1", "snap-1"), RecheckDraw.Uniform("d-1", "snap-1"));
        Assert.NotEqual(RecheckDraw.Uniform("d-1", "snap-1"), RecheckDraw.Uniform("d-1", "snap-2"));
    }

    [Fact]
    public void TheShareDrawnMatchesPi()
    {
        // A recheck share that drifts from π would bias every 1/π count built on it.
        const int n = 20_000;
        var values = Enumerable.Range(0, n).Select(i => RecheckDraw.Uniform($"decision-{i}", "snap-7")).ToArray();

        Assert.All(values, v => Assert.InRange(v, 0.0, 0.9999999999));
        Assert.InRange(values.Average(), 0.49, 0.51);
        Assert.InRange(values.Count(v => v < 0.1) / (double)n, 0.093, 0.107);
    }
}

public class RecheckAndSnapshotTests
{
    private sealed record Item(string Id, string Slice);

    private static readonly Tier Ml = new() { Id = "edge-ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
    private static readonly Tier Teacher = new() { Id = "teacher", Kind = JudgeKind.Llm, Version = "p-3", CostRank = 2, AcceptsHints = true };

    [Fact]
    public async Task ARecheck_IsBlind_AndSettlesNothing()
    {
        JudgeContext? seen = null;
        var teacher = Judge.From<Item, string>(Teacher, (_, c, _) => { seen = c; return ValueTask.FromResult(JudgeVerdict<string>.Answer("LOW")); });
        var lower = Judge.From<Item, string>(Ml, (_, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Abstain("x", new Dictionary<string, string> { ["path"] = "a" })));
        var router = new TieredRouter<Item, string>(new[] { lower, teacher }, SettlementPolicy.Categorical(new[] { "OK" }, Array.Empty<string>()),
            SliceKey.From<Item>("k", i => i.Slice), i => i.Id, () => null);

        var result = await router.RecheckAsync(new Item("d1", "s9"), "d1", "teacher");

        Assert.True(seen!.IsRecheck);
        Assert.Null(seen.Hints);
        Assert.Null(seen.AbstainQuantile);
        Assert.Equal("s9", seen.SliceKey);
        Assert.Equal("LOW", result.Verdict.Value);
        Assert.Equal("p-3", result.Tier.Version);
    }

    [Fact]
    public async Task ARecheckThatFails_IsAResult_NotAnError()
    {
        var teacher = Judge.From<Item, string>(Teacher, (_, _, _) => throw new TimeoutException("upstream saturated"));
        var router = new TieredRouter<Item, string>(new[] { Judge.Constant<Item, string>(Ml, "OK"), teacher },
            SettlementPolicy.Categorical(new[] { "OK" }, Array.Empty<string>()), SliceKey.Single<Item>(), i => i.Id, () => null);

        var result = await router.RecheckAsync(new Item("d2", "s"), "d2", "teacher");

        Assert.False(result.Verdict.Answered);
        Assert.Contains("upstream saturated", result.Verdict.Rationale);
    }

    private static PolicySnapshot Snapshot(params PolicyEntry[] entries) => new()
    {
        Version = "snap-4",
        IssuedAt = new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.FromHours(9)),
        SoftExpiresAt = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero),
        HardExpiresAt = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero),
        SliceKeyVersion = "template-v1",
        Entries = entries,
    };

    private static readonly PolicyEntry A = new()
    {
        TierId = "edge-ml",
        SliceKey = "t-1",
        State = TrustState.Active,
        SampleRates = new Dictionary<string, double> { ["uniform"] = 0.05, ["miss"] = 0.31 },
        AbstainQuantile = 0.05,
        RateSetBy = "miss",
    };
    private static readonly PolicyEntry B = new() { TierId = "edge-ml", SliceKey = "t-0", State = TrustState.Shadow };

    [Fact]
    public void TheSnapshotRoundTrips()
    {
        var original = Snapshot(A, B);

        var read = PolicySnapshotJson.Deserialize(PolicySnapshotJson.Serialize(original));

        Assert.Equal(original.Version, read.Version);
        Assert.Equal(original.IssuedAt, read.IssuedAt);
        Assert.Equal(original.HardExpiresAt, read.HardExpiresAt);
        Assert.Equal("template-v1", read.SliceKeyVersion);
        var a = read.Entries.Single(e => e.SliceKey == "t-1");
        Assert.Equal(TrustState.Active, a.State);
        Assert.Equal(0.31, a.SampleRates!["miss"]);
        Assert.Equal(0.05, a.AbstainQuantile);
        Assert.Equal("miss", a.RateSetBy);
        Assert.Null(read.Entries.Single(e => e.SliceKey == "t-0").SampleRates);
    }

    [Fact]
    public void TheHashNamesTheContent_NotTheOrderItWasBuiltIn()
    {
        var reordered = Snapshot(B, A with { SampleRates = new Dictionary<string, double> { ["miss"] = 0.31, ["uniform"] = 0.05 } });
        var sameInstantOtherZone = Snapshot(A, B) with { IssuedAt = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero) };

        Assert.Equal(PolicySnapshotJson.ContentHash(Snapshot(A, B)), PolicySnapshotJson.ContentHash(reordered));
        Assert.Equal(PolicySnapshotJson.ContentHash(Snapshot(A, B)), PolicySnapshotJson.ContentHash(sameInstantOtherZone));
        Assert.NotEqual(PolicySnapshotJson.ContentHash(Snapshot(A, B)), PolicySnapshotJson.ContentHash(Snapshot(A, B with { State = TrustState.Active })));
        Assert.Matches("^[0-9a-f]{64}$", PolicySnapshotJson.ContentHash(Snapshot(A)));
    }

    [Fact]
    public async Task ASnapshotReadFromJson_RoutesLikeTheOriginal()
    {
        var read = PolicySnapshotJson.Deserialize(PolicySnapshotJson.Serialize(Snapshot(A, B)));
        var router = new TieredRouter<Item, string>(
            new[] { Judge.Constant<Item, string>(Ml, "OK"), Judge.Constant<Item, string>(Teacher, "OK") },
            SettlementPolicy.Categorical(new[] { "OK" }, Array.Empty<string>()),
            SliceKey.From<Item>("template-v1", i => i.Slice), i => i.Id, () => read,
            new TieredRouterOptions { Clock = () => new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) });

        var settled = (await router.RouteAsync(new Item("d3", "t-1"))).Provenance;
        Assert.Equal("edge-ml", settled.TierId);
        Assert.Equal(0.31, settled.Sample!.Probability);
        Assert.Equal("miss", settled.Sample.Knob);
    }
}
