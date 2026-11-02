using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// A tier's standing is earned on the rechecks of the tier above it. When that tier is replaced by a new version, the evidence is
/// no longer taken from the teacher that is there — the records say which version made each recheck, and a lifecycle signal
/// tightens the standing the way an unqualified teacher does, without judging the new teacher.
/// </summary>
public class TeacherVersionChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly LifecyclePolicy Policy = new()
    {
        Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
        Margin = 0.01,
        MinimumEffectiveSample = 300,
        MinimumDwell = TimeSpan.FromDays(7),
        StableWindows = 2,
    };

    private static RecheckRecord Record(string id, string? checkedBy, string? version) => new()
    {
        DecisionId = id,
        TierId = "ml",
        SliceKey = "s",
        DecidedAt = Now,
        Probability = 0.1,
        Outcome = RecheckOutcome.Agreed,
        CheckedBy = checkedBy,
        CheckedByVersion = version,
    };

    private static LedgerCell Healthy() => new()
    {
        TierId = "ml",
        SliceKey = "s",
        WindowStart = Start,
        WindowEnd = Now,
        Rechecks = 500,
        UpperFailures = 0,
        EffectiveSampleSize = 500,
        Agreement = 0.98,
        AgreementLower = 0.97,
        AgreementUpper = 0.99,
        MissesEstimated = 0,
        Caught = 400,
        MissRate = 0,
        MissRateUpper = 0.01,
        MissRateLower = 0,
        Confidence = 0.95,
    };

    [Fact]
    public void ANewVersionOfTheSameTeacher_IsAChange()
    {
        Assert.True(TeacherVersions.Changed([Record("a", "people", "1"), Record("b", "people", "1")], [Record("c", "people", "2")]));
        Assert.True(TeacherVersions.Changed([Record("a", "people", "1")], [Record("b", "people", "1"), Record("c", "people", "2")]));
    }

    [Fact]
    public void TheSameVersions_OrFewer_AreNotAChange()
    {
        Assert.False(TeacherVersions.Changed([Record("a", "people", "1"), Record("b", "people", "2")], [Record("c", "people", "1")]));
        Assert.False(TeacherVersions.Changed([Record("a", "people", "1")], []));
    }

    [Fact]
    public void WithoutAVersionToCompare_NothingChanged()
    {
        // Records written before the version was kept, a first stretch of evidence, and a teacher added beside the old one are not a new version.
        Assert.False(TeacherVersions.Changed([Record("a", "people", null)], [Record("b", "people", "2")]));
        Assert.False(TeacherVersions.Changed([], [Record("b", "people", "2")]));
        Assert.False(TeacherVersions.Changed([Record("a", "people", "1")], [Record("b", "llm", "9")]));
        Assert.False(TeacherVersions.Changed([Record("a", "people", "1")], [Record("b", null, "2"), Record("c", "people", null)]));
    }

    [Fact]
    public void ATeachersVersionIsComparedWithItsOwnEarlierVersions_NotWithAnotherTeachers()
    {
        var before = new[] { Record("a", "people", "1"), Record("b", "llm", "5") };

        Assert.False(TeacherVersions.Changed(before, [Record("c", "people", "1"), Record("d", "llm", "5")]));
        Assert.True(TeacherVersions.Changed(before, [Record("c", "people", "1"), Record("d", "llm", "6")]));
        Assert.True(TeacherVersions.Changed(before, [Record("c", "people", "5")]));              // "5" was the other teacher's version, not people's
    }

    [Fact]
    public void AStableTier_FallsBackToActive_WhenItsTeacherChangedVersion()
    {
        var stable = new SliceStanding(TrustState.Stable, Start, "v1");

        var held = TrustLifecycle.Tighten(stable, Healthy(), new LiveSignals("v1"), Policy, Now);
        var changed = TrustLifecycle.Tighten(stable, Healthy(), new LiveSignals("v1", TeacherVersionChanged: true), Policy, Now);

        Assert.False(held.Moved);
        Assert.Equal(TrustState.Active, changed.To);
        Assert.False(changed.AwaitsApproval);          // tightening never waits
        Assert.Contains("new version", changed.Reason);
    }

    [Fact]
    public void AnActiveTiersRunTowardStable_StartsOver_AndAShadowTierIsUntouched()
    {
        var signals = new LiveSignals("v1", TeacherVersionChanged: true);

        var active = TrustLifecycle.Tighten(new SliceStanding(TrustState.Active, Start, "v1", PassingWindows: 1), Healthy(), signals, Policy, Now);
        var shadow = TrustLifecycle.Tighten(new SliceStanding(TrustState.Shadow, Start, "v1"), Healthy(), signals, Policy, Now);

        Assert.False(active.Moved);
        Assert.Equal(0, active.Standing.PassingWindows);
        Assert.Contains("starts over", active.Reason);
        Assert.False(shadow.Moved);
    }

    [Fact]
    public void AnUnqualifiedTeacher_IsStillSaidToBeUnqualified_WhenItsVersionChangedToo()
    {
        var stable = new SliceStanding(TrustState.Stable, Start, "v1");

        var both = TrustLifecycle.Tighten(stable, Healthy(), new LiveSignals("v1", TeacherDisqualified: true, TeacherVersionChanged: true), Policy, Now);

        Assert.Equal(TrustState.Active, both.To);
        Assert.Contains("unqualified", both.Reason);
    }

    [Fact]
    public async Task TheRecheckRecord_NamesTheVersionOfTheTeacherThatMadeIt()
    {
        var ml = new Tier { Id = "ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
        var top = new Tier { Id = "top", Kind = JudgeKind.Human, Version = "7", CostRank = 2 };
        var policy = SettlementPolicy.Categorical(new[] { "OK" }, Array.Empty<string>());
        var snapshot = new PolicySnapshot
        {
            Version = "v",
            IssuedAt = Now,
            SoftExpiresAt = Now.AddHours(1),
            HardExpiresAt = Now.AddHours(2),
            Entries = [new PolicyEntry { TierId = "ml", SliceKey = "s", State = TrustState.Active, SampleRates = new Dictionary<string, double> { ["uniform"] = 1.0 } }],
        };
        var router = new TieredRouter<string, string>(
            [Judge.Constant<string, string>(ml, "OK", 0.9), Judge.Constant<string, string>(top, "OK")],
            policy, SliceKey.From<string>("k", _ => "s"), id => id, () => snapshot, new TieredRouterOptions { Clock = () => Now });

        var decision = await router.DecideAsync("d1");
        var recheck = await router.RecheckAsync("d1", "d1", "top");
        var record = RecheckRecords.From(decision, recheck, policy);

        Assert.Equal("top", record.CheckedBy);
        Assert.Equal("7", record.CheckedByVersion);
    }

    [Fact]
    public void ARoutedPoolAnswer_CarriesTheCheckersVersion_IntoTheLedgerRecord()
    {
        var people = new Tier { Id = "people", Kind = JudgeKind.Human, Version = "3", CostRank = 3 };
        var policy = SettlementPolicy.Categorical(settleable: ["ok"], mustCatch: ["danger"]);
        var item = new PoolItem
        {
            DecisionId = "d1",
            TierId = "ml",
            SliceKey = "s",
            DecidedAt = Now.AddDays(-10),
            Probability = 0.5,
            InputRef = "in/d1",
            ExpiresAt = Now.AddDays(-3),
        };
        var shown = new PoolPresentation("d1", "ml", "s", "in/d1");

        var routed = PoolAnswerRouting.Route(shown, people, "r-1", PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForRecheck("ok"), policy, Now.AddDays(-9));
        var account = RecheckPool.Account([new PoolEntry(item, null, routed.Response)], Now);

        Assert.Equal("3", routed.Response.CheckedByVersion);
        Assert.Equal("3", Assert.Single(account.Records).CheckedByVersion);
        Assert.Equal("people", account.Records[0].CheckedBy);
    }
}
