using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// A tier in shadow answers what it would have settled while a tier above settles the decision: that pair is a
/// check at probability 1, and the shadow window fills the same ledger cell the rechecks of a settling tier do.
/// </summary>
public class ShadowRecordsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tier Ml = new() { Id = "edge-ml", Kind = JudgeKind.Ml, Version = "m1", CostRank = 1 };
    private static readonly Tier Teacher = new() { Id = "teacher", Kind = JudgeKind.Llm, Version = "t1", CostRank = 2 };
    private static readonly ISettlementPolicy<string> Policy = SettlementPolicy.Categorical(new[] { "OK", "LOW" }, new[] { "HIGH" });

    private sealed record Item(int N)
    {
        public string Id => $"d{N}";
        // True: mostly OK, every 20th LOW, offsets 1 and 2 of every 50 HIGH.
        public string Truth => N % 50 is 1 or 2 ? "HIGH" : N % 20 == 0 ? "LOW" : "OK";
        // The edge model: right, except it calls every 20th "OK" (a disagreement) and misses the HIGH at offset 1 as "OK" (a miss).
        public string MlSays => N % 50 == 2 ? "HIGH" : N % 20 == 0 ? "OK" : N % 50 == 1 ? "OK" : Truth;
    }

    private static PolicySnapshot Snapshot(TrustState state, TimeSpan hardLife) => new()
    {
        Version = "snap-1",
        IssuedAt = Now.AddHours(-1),
        SoftExpiresAt = Now.AddHours(1),
        HardExpiresAt = Now + hardLife,
        Entries = [new PolicyEntry { TierId = Ml.Id, SliceKey = "all", State = state }],
    };

    private static TieredRouter<Item, string> Router(TrustState state, TimeSpan hardLife) => new(
        [
            Judge.From<Item, string>(Ml, (i, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Answer(i.MlSays))),
            Judge.From<Item, string>(Teacher, (i, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Answer(i.Truth))),
        ],
        Policy, SliceKey.From<Item>("k1", _ => "all"), i => i.Id, () => Snapshot(state, hardLife),
        new TieredRouterOptions { Clock = () => Now });

    [Fact]
    public async Task ATierInShadow_GetsOneCheckedRecordPerSettleableAnswer_AndTheLedgerCountsThemLikeRechecks()
    {
        var router = Router(TrustState.Shadow, TimeSpan.FromHours(2));
        var records = new List<RecheckRecord>();
        int settleable = 0, disagreements = 0, misses = 0;

        for (var n = 0; n < 5000; n++)
        {
            var item = new Item(n);
            var decision = await router.DecideAsync(item);
            records.AddRange(RecheckRecords.FromShadow(decision, Policy));

            // The ML tier's must-catch answer escalates (it is not shadowed); everything else it says is shadowed.
            if (item.MlSays == "HIGH") continue;
            settleable++;
            if (item.MlSays != item.Truth) disagreements++;
            if (item.Truth == "HIGH") misses++;
        }

        Assert.Equal(settleable, records.Count);
        Assert.All(records, r =>
        {
            Assert.Equal(Ml.Id, r.TierId);
            Assert.Equal(1.0, r.Probability);
            Assert.Equal(RecheckRecords.ShadowKnob, r.Knob);
            Assert.Equal(Teacher.Id, r.CheckedBy);
        });
        Assert.Equal(disagreements, records.Count(r => r.Outcome == RecheckOutcome.Disagreed));
        Assert.Equal(misses, records.Count(r => r.UpperMustCatch));

        // π = 1 for all of them: the cell's agreement is the plain share, and the misses are the ones the teacher made must-catch.
        var cell = TrustLedger.Compute(records, Ml.Id, "all", Now.AddDays(-1), Now.AddDays(1), caught: 100);
        Assert.Equal((settleable - disagreements) / (double)settleable, cell.Agreement!.Value, 9);
        Assert.Equal(misses, cell.MissesEstimated, 9);
    }

    [Fact]
    public async Task ATopTierThatSettles_IsTheCheck_EvenUnderAnExpiredSnapshot()
    {
        // The top tier needs no policy to vouch for it, so its answer checks the shadow tier whatever the snapshot's age.
        var router = Router(TrustState.Shadow, TimeSpan.FromHours(-1));   // hard-expired

        var decision = await router.DecideAsync(new Item(3));

        Assert.Single(RecheckRecords.FromShadow(decision, Policy));
    }

    [Fact]
    public async Task ADecisionSettledUnverified_IsNotACheck()
    {
        // Active tier, expired snapshot, nothing above answers: the held answer is used, marked unverified — nothing vouched for it.
        var router = new TieredRouter<Item, string>(
            [
                Judge.From<Item, string>(Ml, (i, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Answer(i.MlSays))),
                Judge.From<Item, string>(Teacher, (i, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Abstain("down"))),
            ],
            Policy, SliceKey.From<Item>("k1", _ => "all"), i => i.Id, () => Snapshot(TrustState.Active, TimeSpan.FromHours(-1)),
            new TieredRouterOptions { Clock = () => Now });

        var decision = await router.DecideAsync(new Item(3));

        Assert.True(decision.Prediction.Provenance.Unverified);
        Assert.Empty(RecheckRecords.FromShadow(decision, Policy));
    }

    [Fact]
    public async Task ASettlingTier_HasNoShadowRecords()
    {
        var router = Router(TrustState.Active, TimeSpan.FromHours(2));

        var decision = await router.DecideAsync(new Item(3));

        Assert.Equal(Ml.Id, decision.Prediction.Provenance.TierId);
        Assert.Empty(RecheckRecords.FromShadow(decision, Policy));
    }
}
