using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class ProbeAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
    private static readonly Tier Teacher = new() { Id = "teacher", Kind = JudgeKind.Llm, Version = "p-3", CostRank = 2, AcceptsHints = true };
    private static readonly Tier Closed = new() { Id = "closed", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };
    private static readonly ISettlementPolicy<string> Policy = SettlementPolicy.Categorical(new[] { "OK", "LOW" }, new[] { "HIGH" });

    private sealed record Case(string Id, string BlindAnswer);

    private static TieredRouter<Case, string> Router()
        => new(
            [Judge.Constant<Case, string>(Ml, "OK"),
             Judge.From<Case, string>(Teacher, (input, context, _) =>
                 ValueTask.FromResult(JudgeVerdict<string>.Answer(context.Hints?.GetValueOrDefault(JudgeContext.LowerAnswerHint) ?? input.BlindAnswer))),
             Judge.Constant<Case, string>(Closed, "LOW")],
            Policy, SliceKey.From<Case>("k", _ => "s"), c => c.Id, () => null,
            new TieredRouterOptions { Clock = () => Now });

    private static ProbeCandidate Candidate(string id, ProbeMethod method, string tier, string? judgedBy = null)
        => new() { DecisionId = id, SliceKey = "s", Target = new JudgeIdentity(tier, judgedBy), Method = method, Pool = "p" };

    private static ProbePlan Plan(params ProbeCandidate[] candidates)
        => ProbeSelector.Select(candidates, new ProbePolicy { PoolBudgets = new Dictionary<string, double> { ["p"] = 1000 } }, "plan-1");

    private static MachineProbeAdapter<Case, string> Machine(bool anchoring = true)
        => new(Router(), ["ml", "teacher"], (c, _) => ValueTask.FromResult(new Case(c.DecisionId, "HIGH")),
            anchoring ? (_, _) => ValueTask.FromResult("OK") : null);

    private static PoolProbeAdapter<string> People(InMemoryRecheckPoolStore store)
        => new(store, target => target.TierId == "people",
            probe => new ProbeSubject("ml", Now, $"in/{probe.Candidate.DecisionId}", Now.AddDays(7)) { Risk = 0.4 });

    [Fact]
    public async Task OnePlan_IsPutByEveryKindOfJudge_EachInItsOwnWay()
    {
        var store = new InMemoryRecheckPoolStore();
        var plan = Plan(
            Candidate("d1", ProbeMethod.Recheck, "teacher"),
            Candidate("d2", ProbeMethod.Anchoring, "teacher"),
            Candidate("d3", ProbeMethod.Recheck, "people"),
            Candidate("d4", ProbeMethod.Plant, "people"));

        var run = await ProbeDispatch.RunAsync<string>(plan, [Machine(), People(store)]);

        var byId = run.Deliveries.ToDictionary(d => d.Probe.Candidate.DecisionId);
        Assert.Equal(ProbeDeliveryKind.Judged, byId["d1"].Kind);
        Assert.Equal("teacher", byId["d1"].Result!.Tier.Id);
        Assert.False(byId["d1"].Result!.Shown);                                        // blind
        Assert.Equal("HIGH", byId["d1"].Result!.Verdict.Value);
        Assert.Equal(ProbeDeliveryKind.Judged, byId["d2"].Kind);
        Assert.True(byId["d2"].Result!.Shown);                                         // shown the lower answer
        Assert.Equal("OK", byId["d2"].Result!.Verdict.Value);
        Assert.Equal(ProbeDeliveryKind.Queued, byId["d3"].Kind);
        Assert.Equal(ProbeDeliveryKind.Queued, byId["d4"].Kind);
        Assert.Empty(run.Declined);
    }

    [Fact]
    public async Task APooledProbe_CarriesThePiItWasDrawnWith_AndItsMethodAsTheItemsKind()
    {
        var store = new InMemoryRecheckPoolStore();
        // Budget 1 for 20 candidates: each is drawn at 1/20 — find the drawn ones and check the pool holds π, not 1.
        var candidates = Enumerable.Range(0, 400).Select(i => Candidate($"d{i}", i % 2 == 0 ? ProbeMethod.Recheck : ProbeMethod.Plant, "people")).ToArray();
        var plan = ProbeSelector.Select(candidates,
            new ProbePolicy { PoolBudgets = new Dictionary<string, double> { ["p"] = 40 } }, "plan-2");

        var run = await ProbeDispatch.RunAsync<string>(plan, [People(store)]);

        Assert.NotEmpty(run.Deliveries);
        var entries = new List<PoolEntry>();
        await foreach (var e in store.ReadAsync(Now.AddDays(-1), Now.AddDays(1)))
            entries.Add(e);
        Assert.Equal(run.Deliveries.Count, entries.Count);
        foreach (var d in run.Deliveries)
        {
            var item = entries.Single(e => e.Item.DecisionId == d.Probe.Candidate.DecisionId).Item;
            Assert.Equal(d.Probe.Probability, item.Probability);
            Assert.Equal(d.Probe.Candidate.Method == ProbeMethod.Plant ? PoolItemKind.Plant : PoolItemKind.Recheck, item.Kind);
            Assert.Equal("ml", item.TierId);                                           // the tier measured, not the one asked
            Assert.Equal(0.4, item.Risk);
            Assert.DoesNotContain("people", item.InputRef);
        }
    }

    [Fact]
    public async Task AProbeNobodyPuts_IsDeclinedAndCounted_NotDropped()
    {
        var store = new InMemoryRecheckPoolStore();
        var plan = Plan(
            Candidate("d1", ProbeMethod.Repeat, "people"),               // a pool makes repeats from answered items
            Candidate("d2", ProbeMethod.Recheck, "stranger"),            // no tier of that name
            Candidate("d3", ProbeMethod.Anchoring, "people"),            // a pool never shows another's answer
            Candidate("d4", ProbeMethod.Recheck, "teacher"));

        var run = await ProbeDispatch.RunAsync<string>(plan, [Machine(), People(store)]);

        Assert.Equal(4, run.Deliveries.Count);
        Assert.Equal(["d1", "d2", "d3"], run.Deliveries.Where(d => d.Kind == ProbeDeliveryKind.Declined).Select(d => d.Probe.Candidate.DecisionId).Order());
        Assert.All(run.Deliveries.Where(d => d.Kind == ProbeDeliveryKind.Declined), d => Assert.NotNull(d.Reason));
        Assert.Equal(1, run.Declined[("p", ProbeMethod.Repeat)]);
        Assert.Equal(1, run.Declined[("p", ProbeMethod.Recheck)]);
        Assert.Equal(1, run.Declined[("p", ProbeMethod.Anchoring)]);
    }

    [Fact]
    public async Task OnlyTheDrawnProbesAreDispatched()
    {
        var plan = ProbeSelector.Select(
            Enumerable.Range(0, 200).Select(i => Candidate($"d{i}", ProbeMethod.Recheck, "teacher")).ToArray(),
            new ProbePolicy { PoolBudgets = new Dictionary<string, double> { ["p"] = 20 } }, "plan-3");

        var run = await ProbeDispatch.RunAsync<string>(plan, [Machine()]);

        Assert.InRange(run.Deliveries.Count, 1, 199);
        Assert.Equal(plan.Drawn.Count(), run.Deliveries.Count);
        Assert.All(run.Deliveries, d => Assert.True(d.Probe.Drawn));
    }

    [Fact]
    public async Task AMachineAdapterWithoutAnAnchoringAnswer_DoesNotAcceptAnchoring()
    {
        var plan = Plan(Candidate("d1", ProbeMethod.Anchoring, "teacher"));

        var run = await ProbeDispatch.RunAsync<string>(plan, [Machine(anchoring: false)]);

        Assert.Equal(ProbeDeliveryKind.Declined, Assert.Single(run.Deliveries).Kind);
    }

    [Fact]
    public async Task APooledItem_IsPooledOnce_ASecondProbeForItSaysSo()
    {
        var store = new InMemoryRecheckPoolStore();
        var plan = Plan(Candidate("d1", ProbeMethod.Recheck, "people"));
        var adapter = People(store);

        var first = await ProbeDispatch.RunAsync<string>(plan, [adapter]);
        var second = await ProbeDispatch.RunAsync<string>(plan, [adapter]);

        Assert.Null(first.Deliveries.Single().Reason);
        Assert.Contains("Already in the pool", second.Deliveries.Single().Reason);
    }
}
