using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class RecheckPoolTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hold = TimeSpan.FromHours(1);

    private static PoolItem Item(int i, double probability = 0.1, string slice = "s", string input = "in") => new()
    {
        DecisionId = $"d{i}",
        TierId = "ml",
        SliceKey = slice,
        DecidedAt = Start.AddMinutes(i),
        Probability = probability,
        InputRef = $"{input}/{i}",
        ExpiresAt = Start.AddDays(7),
    };

    private static PoolResponse Answer(PoolPresentation p, DateTimeOffset at, RecheckOutcome outcome = RecheckOutcome.Agreed, bool mustCatch = false) => new()
    {
        DecisionId = p.DecisionId,
        TierId = p.TierId,
        CheckedBy = "people",
        ReviewerKey = "r-1",
        RespondedAt = at,
        Kind = PoolResponseKind.Answered,
        Outcome = outcome,
        UpperMustCatch = mustCatch,
    };

    private static async Task<InMemoryRecheckPoolStore> Pool(IEnumerable<PoolItem> items)
    {
        var store = new InMemoryRecheckPoolStore();
        foreach (var item in items)
            Assert.True(await store.AddAsync(item));
        return store;
    }

    [Fact]
    public async Task TheOrderShown_DependsOnTheDecisionAndTheSession_NeverOnTheItem()
    {
        var now = Start.AddDays(1);
        var a = await Pool(Enumerable.Range(0, 50).Select(i => Item(i)));
        var b = await Pool(Enumerable.Range(0, 50).Select(i => Item(i, probability: 0.9, input: "other")));   // same decisions, other contents

        var shownA = (await RecheckPool.PresentAsync(a, "r-1", "session-1", 10, now, Hold)).Select(p => p.DecisionId).ToList();
        var shownB = (await RecheckPool.PresentAsync(b, "r-1", "session-1", 10, now, Hold)).Select(p => p.DecisionId).ToList();
        Assert.Equal(shownA, shownB);
        Assert.NotEqual(Enumerable.Range(0, 10).Select(i => $"d{i}"), shownA);                                 // not in arrival order

        var c = await Pool(Enumerable.Range(0, 50).Select(i => Item(i)));
        var other = (await RecheckPool.PresentAsync(c, "r-1", "session-2", 10, now, Hold)).Select(p => p.DecisionId).ToList();
        Assert.NotEqual(shownA, other);
    }

    [Fact]
    public async Task AHeldItem_IsNotShownToAnotherJudge_UntilTheHoldEnds_AndAnsweredOrExpiredOnesNeverAgain()
    {
        var now = Start.AddDays(1);
        var store = await Pool([Item(1), Item(2), Item(3) with { ExpiresAt = now }]);

        var first = await RecheckPool.PresentAsync(store, "r-1", "s1", 1, now, Hold);
        var second = await RecheckPool.PresentAsync(store, "r-2", "s1", 5, now, Hold);
        Assert.Single(first);
        Assert.Equal(["d1", "d2"], first.Concat(second).Select(p => p.DecisionId).Order());                // d3 has expired

        // r-1 comes back within the hold: its own item first.
        Assert.Equal(first[0].DecisionId, (await RecheckPool.PresentAsync(store, "r-1", "s9", 1, now.AddMinutes(5), Hold))[0].DecisionId);

        await store.RespondAsync(Answer(first[0], now));
        Assert.DoesNotContain(await RecheckPool.PresentAsync(store, "r-2", "s3", 5, now.AddHours(3), Hold), p => p.DecisionId == first[0].DecisionId);
        // r-2 held it again just now (at +3 h, for an hour); once that runs out, it is back for anyone.
        Assert.Contains(await RecheckPool.PresentAsync(store, "r-3", "s4", 5, now.AddHours(5), Hold), p => p.DecisionId == second[0].DecisionId);
    }

    [Fact]
    public async Task AWindow_GivesNothingWhileAnItemCouldStillBeReached_ThenRechecksAtPiTimesTheReach()
    {
        var store = await Pool(Enumerable.Range(0, 10).Select(i => Item(i, probability: 0.1)).Append(Item(20, probability: 0.5, slice: "t")));
        var now = Start.AddDays(1);
        var shown = await RecheckPool.PresentAsync(store, "r-1", "s1", 4, now, Hold, tierId: "ml");
        var inS = shown.Where(p => p.SliceKey == "s").ToList();
        await store.RespondAsync(Answer(inS[0], now, RecheckOutcome.Disagreed, mustCatch: true));
        await store.RespondAsync(Answer(inS[1], now) with { Kind = PoolResponseKind.Skipped, Outcome = null });
        foreach (var p in shown.Skip(2))
            await store.RespondAsync(Answer(p, now));

        var open = await RecheckPool.AccountAsync(store, Start, Start.AddDays(1), now);
        Assert.False(open.Closed);
        Assert.Empty(open.Records);

        var closed = await RecheckPool.AccountAsync(store, Start, Start.AddDays(1), Start.AddDays(8));
        Assert.True(closed.Closed);
        var reachS = closed.Reach.Single(r => r.SliceKey == "s");
        Assert.Equal((10, shown.Count(p => p.SliceKey == "s")), (reachS.Pooled, reachS.Reached));
        var record = closed.Records.Single(r => r.DecisionId == inS[0].DecisionId);
        Assert.Equal(0.1 * reachS.Rate, record.Probability, 12);
        Assert.Equal((RecheckOutcome.Disagreed, true, "people", true), (record.Outcome, record.UpperMustCatch, record.CheckedBy, record.Blind));
        Assert.Equal(RecheckOutcome.UpperFailed, closed.Records.Single(r => r.DecisionId == inS[1].DecisionId).Outcome);
        Assert.Equal(shown.Count, closed.Records.Count);                                                  // unreached ones are left out

        // The reached rechecks stand for every pooled decision: Σ 1/(π·r) over them = Σ 1/π over the pooled.
        var standsFor = closed.Records.Where(r => r.SliceKey == "s").Sum(r => 1 / r.Probability);
        Assert.Equal(10 / 0.1, standsFor, 9);
    }

    [Fact]
    public async Task TheJsonlPool_KeepsItemsAndResponsesOnce_AndTheLatestHold()
    {
        var dir = Path.Combine(Path.GetTempPath(), "honeai-pool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonlRecheckPoolStore(dir);
            Assert.True(await store.AddAsync(Item(1)));
            Assert.False(await store.AddAsync(Item(1)));
            var now = Start.AddDays(1);
            await store.LeaseAsync(new PoolLease("d1", "ml", "r-1", now));
            await store.LeaseAsync(new PoolLease("d1", "ml", "r-2", now.AddHours(1)));
            var response = Answer(new PoolPresentation("d1", "ml", "s", "in/1"), now);
            Assert.True(await store.RespondAsync(response));
            Assert.False(await store.RespondAsync(response with { Outcome = RecheckOutcome.Disagreed }));

            var entries = new List<PoolEntry>();
            await foreach (var e in new JsonlRecheckPoolStore(dir).ReadAsync(Start, now))
                entries.Add(e);
            var entry = Assert.Single(entries);
            Assert.Equal("r-2", entry.Lease!.ReviewerKey);
            Assert.Equal(RecheckOutcome.Agreed, entry.Response!.Outcome);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
