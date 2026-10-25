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
    public async Task ASession_MixesInPlants_Repeats_AndSecondOpinions_AtTheirShares_FillingTheRestWithRechecks()
    {
        var now = Start.AddDays(10);
        var items = Enumerable.Range(0, 40).Select(i => Item(i) with { ExpiresAt = Start.AddDays(30) })
            .Concat(Enumerable.Range(100, 10).Select(i => Item(i) with { Kind = PoolItemKind.Plant, ExpiresAt = Start.AddDays(30) }));
        var store = await Pool(items);
        // d0..d4 answered by r-1 nine days ago (repeat material for r-1), d5..d9 by r-2 (second opinions for r-1).
        foreach (var (i, who) in Enumerable.Range(0, 10).Select(i => (i, i < 5 ? "r-1" : "r-2")))
            await store.RespondAsync(Answer(new PoolPresentation($"d{i}", "ml", "s", $"in/{i}"), Start.AddDays(1)) with { ReviewerKey = who });

        var mix = new SessionMix { PlantShare = 0.2, RepeatShare = 0.1, SecondOpinionShare = 0.1 };
        var shown = await RecheckPool.PresentAsync(store, "r-1", "s1", 20, now, Hold, mix: mix);

        Assert.Equal(20, shown.Count);
        Assert.Equal(4, shown.Count(p => p.Purpose == PoolPurpose.Plant));
        Assert.Equal(2, shown.Count(p => p.Purpose == PoolPurpose.Repeat));
        Assert.Equal(2, shown.Count(p => p.Purpose == PoolPurpose.SecondOpinion));
        Assert.Equal(12, shown.Count(p => p.Purpose == PoolPurpose.Recheck));
        Assert.All(shown.Where(p => p.Purpose == PoolPurpose.Repeat), p => Assert.True(int.Parse(p.DecisionId[1..]) < 5));
        Assert.All(shown.Where(p => p.Purpose == PoolPurpose.SecondOpinion), p => Assert.InRange(int.Parse(p.DecisionId[1..]), 5, 9));
        Assert.All(shown.Where(p => p.Purpose == PoolPurpose.Recheck), p => Assert.InRange(int.Parse(p.DecisionId[1..]), 10, 39));
        // Mixed in among the rechecks, not stacked at one end: the first half already holds more than one purpose.
        Assert.True(shown.Take(10).Select(p => p.Purpose).Distinct().Count() > 1);

        // Too soon for a repeat: its share goes to rechecks. A plant this judge answered is not shown again.
        var plant = shown.First(p => p.Purpose == PoolPurpose.Plant);
        await store.RespondAsync(Answer(plant, now) with { Purpose = PoolPurpose.Plant, Outcome = null });
        var early = await RecheckPool.PresentAsync(store, "r-1", "s2", 20, Start.AddDays(5), Hold, mix: mix);
        Assert.DoesNotContain(early, p => p.Purpose == PoolPurpose.Repeat);
        Assert.DoesNotContain(early, p => p.DecisionId == plant.DecisionId && p.Purpose == PoolPurpose.Plant);

        // Plants, repeats and second opinions stay out of the tier's measurement.
        var account = RecheckPool.Account(await Entries(store), Start.AddDays(40));
        Assert.Equal(40, account.Reach.Single().Pooled);
        Assert.DoesNotContain(account.Records, r => r.DecisionId == plant.DecisionId);
        Assert.Throws<InvalidOperationException>(() => RecheckPool.PresentAsync(store, "r-1", "s3", 5, now, Hold,
            mix: new SessionMix { PlantShare = 0.6, RepeatShare = 0.4 }).GetAwaiter().GetResult());
    }

    [Fact]
    public void AnAnswer_BecomesTheResponse_TheRecheckResult_AndTheDrawItsLabelIsWeightedBy()
    {
        var policy = SettlementPolicy.Categorical(settleable: ["ok", "minor"], mustCatch: ["danger"]);
        var shown = new PoolPresentation("d1", "ml", "s", "in/1");
        var at = Start.AddDays(2);

        var disagreed = PoolAnswers.Response(shown, "people", "r-1", JudgeVerdict<string>.Answer("danger"), "ok", policy, at);
        Assert.Equal((PoolResponseKind.Answered, (RecheckOutcome?)RecheckOutcome.Disagreed, true), (disagreed.Kind, disagreed.Outcome, disagreed.UpperMustCatch));
        var passed = PoolAnswers.Response(shown, "people", "r-1", JudgeVerdict<string>.Abstain("cannot tell"), "ok", policy, at);
        Assert.Equal((PoolResponseKind.Skipped, (RecheckOutcome?)null), (passed.Kind, passed.Outcome));
        var repeat = PoolAnswers.Response(shown with { Purpose = PoolPurpose.Repeat }, "people", "r-1", JudgeVerdict<string>.Answer("ok"), "ok", policy, at);
        Assert.Equal((PoolPurpose.Repeat, (RecheckOutcome?)null), (repeat.Purpose, repeat.Outcome));          // a repeat is not a recheck

        var tier = new Tier { Id = "people", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };
        var result = PoolAnswers.Result(shown, tier, JudgeVerdict<string>.Answer("ok", judgedBy: "r-1"), at);
        Assert.False(result.Shown);
        Assert.Equal(PlantOutcome.Correct, PlantRecords.From(result, "ok", "s", policy).Outcome);

        var draw = PoolAnswers.ReachedDraw(Item(1, probability: 0.2), new PoolReach("ml", "s", 10, 4));
        Assert.Equal((0.2 * 0.4, true), (draw.Probability, draw.Drawn));
        Assert.Throws<ArgumentException>(() => PoolAnswers.ReachedDraw(Item(1), new PoolReach("ml", "t", 10, 4)));
    }

    [Fact]
    public async Task APerJudgeResponse_NeedsAReviewer_AndIsRecordedOncePerJudge()
    {
        var store = await Pool([Item(1)]);
        var shown = new PoolPresentation("d1", "ml", "s", "in/1", PoolPurpose.SecondOpinion);
        var second = Answer(shown, Start) with { Purpose = PoolPurpose.SecondOpinion, Outcome = null };
        Assert.True(await store.RespondAsync(second with { ReviewerKey = "r-2" }));
        Assert.True(await store.RespondAsync(second with { ReviewerKey = "r-3" }));
        Assert.False(await store.RespondAsync(second with { ReviewerKey = "r-2" }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.RespondAsync(second with { ReviewerKey = null }));
        var entry = Assert.Single(await Entries(store));
        Assert.Null(entry.Response);
        Assert.Equal(2, entry.ResponsesOf(PoolPurpose.SecondOpinion).Count());
    }

    private static async Task<List<PoolEntry>> Entries(IRecheckPoolStore store)
    {
        var entries = new List<PoolEntry>();
        await foreach (var e in store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
            entries.Add(e);
        return entries;
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
