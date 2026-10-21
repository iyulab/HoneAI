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
/// From the router to the ledger: decisions routed with known truth, the drawn ones rechecked
/// blind at the teacher, recorded, and counted — and the ledger lands on the truth.
/// </summary>
public class RecheckWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tier Ml = new() { Id = "edge-ml", Kind = JudgeKind.Ml, Version = "m1", CostRank = 1 };
    private static readonly Tier Teacher = new() { Id = "teacher", Kind = JudgeKind.Llm, Version = "t1", CostRank = 2 };
    private static readonly ISettlementPolicy<string> Policy = SettlementPolicy.Categorical(new[] { "OK", "LOW" }, new[] { "HIGH" });

    private sealed record Item(int N)
    {
        public string Id => $"d{N}";
        // What is true: mostly OK, every 20th LOW, every 50th (offset 1, 2) HIGH.
        public string Truth => N % 50 is 1 or 2 ? "HIGH" : N % 20 == 0 ? "LOW" : "OK";
        // What the edge model says: right, except it calls every 20th "OK" and misses the HIGHs at offset 1.
        public string MlSays => N % 50 == 2 ? "HIGH" : N % 20 == 0 ? "OK" : N % 50 == 1 ? "OK" : Truth;
    }

    private static PolicySnapshot Snapshot() => new()
    {
        Version = "snap-7",
        IssuedAt = Now.AddHours(-1),
        SoftExpiresAt = Now.AddHours(1),
        HardExpiresAt = Now.AddHours(2),
        Entries = new[]
        {
            new PolicyEntry
            {
                TierId = Ml.Id, SliceKey = "all", State = TrustState.Active,
                SampleRates = new Dictionary<string, double> { ["uniform"] = 0.05, ["slice-watch"] = 0.3 },
            },
        },
    };

    private static TieredRouter<Item, string> Router() => new(
        new[]
        {
            Judge.From<Item, string>(Ml, (i, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Answer(i.MlSays))),
            Judge.From<Item, string>(Teacher, (i, _, _) => ValueTask.FromResult(JudgeVerdict<string>.Answer(i.Truth))),
        },
        Policy, SliceKey.From<Item>("k1", _ => "all"), i => i.Id, Snapshot,
        new TieredRouterOptions { Clock = () => Now });

    [Fact]
    public async Task RoutedDecisions_Rechecked_Recorded_AndCounted_LandOnTheTruth()
    {
        var router = Router();
        var store = new InMemoryRecheckStore();
        var caught = 0;
        int settledByMl = 0, settledAgreeing = 0, settledMustCatch = 0;

        for (var n = 0; n < 5000; n++)
        {
            var item = new Item(n);
            var decision = await router.DecideAsync(item);
            caught += RecheckRecords.CaughtBy(decision, Policy).Count(t => t == Ml.Id);

            if (decision.Prediction.Provenance.TierId != Ml.Id)
                continue;
            settledByMl++;
            if (decision.Prediction.Value == item.Truth) settledAgreeing++;
            if (item.Truth == "HIGH") settledMustCatch++;

            if (decision.Prediction.Provenance.Sample is { Drawn: true })
            {
                var recheck = await router.RecheckAsync(item, decision.DecisionId, Teacher.Id);
                Assert.True(await store.AppendAsync(RecheckRecords.From(decision, recheck, Policy)));
            }
        }

        var records = new List<RecheckRecord>();
        await foreach (var r in store.ReadAsync(Ml.Id, "all", Now, Now.AddSeconds(1)))
            records.Add(r);
        var cell = TrustLedger.Compute(records, Ml.Id, "all", Now, Now.AddSeconds(1), caught);

        var trueAgreement = (double)settledAgreeing / settledByMl;           // ≈ 0.93
        var trueMissRate = (double)settledMustCatch / (settledMustCatch + caught); // 100 / 200
        Assert.Equal(100, caught);
        Assert.InRange(cell.Agreement!.Value, trueAgreement - 0.02, trueAgreement + 0.02);
        Assert.InRange(trueAgreement, cell.AgreementLower!.Value, cell.AgreementUpper!.Value);
        Assert.InRange(cell.MissRate!.Value, trueMissRate - 0.1, trueMissRate + 0.1);
        Assert.True(cell.MissRateUpper >= trueMissRate);
        Assert.All(records, r => Assert.Equal(("teacher", 0.3, "slice-watch"), (r.CheckedBy, r.Probability, r.Knob)));

        // The promise counted on the uniform knob alone: its own 5% sub-sample, weighted at 1/0.05.
        var uniform = TrustLedger.Compute(records, Ml.Id, "all", Now, Now.AddSeconds(1), caught, knob: "uniform");
        Assert.InRange(uniform.Rechecks, settledByMl * 0.035, settledByMl * 0.065);
        Assert.InRange(uniform.Agreement!.Value, trueAgreement - 0.05, trueAgreement + 0.05);
        Assert.True(uniform.EffectiveSampleSize < cell.EffectiveSampleSize);
    }

    [Fact]
    public async Task ADecisionNotDrawn_CannotBecomeARecheckRecord()
    {
        var router = Router();
        RoutedDecision<string>? notDrawn = null;
        Item? item = null;
        for (var n = 0; notDrawn is null; n++)
        {
            item = new Item(n);
            var d = await router.DecideAsync(item);
            if (d.Prediction.Provenance.Sample is { Drawn: false })
                notDrawn = d;
        }

        var recheck = await router.RecheckAsync(item!, notDrawn.DecisionId, Teacher.Id);

        Assert.Throws<ArgumentException>(() => RecheckRecords.From(notDrawn, recheck, Policy));
    }

    [Fact]
    public async Task TheDecision_CarriesItsTime_AndTheDrawItsKnobRates()
    {
        var decision = await Router().DecideAsync(new Item(3));

        Assert.Equal(Now, decision.DecidedAt);
        Assert.Equal(new Dictionary<string, double> { ["uniform"] = 0.05, ["slice-watch"] = 0.3 },
            decision.Prediction.Provenance.Sample!.KnobRates);
    }
}
