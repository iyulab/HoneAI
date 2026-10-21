using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class PolicySnapshotIssuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly IssuePolicy Policy = new() { SoftLife = TimeSpan.FromDays(1), HardLife = TimeSpan.FromDays(3), ActiveRate = 0.3 };

    private static SliceStanding In(TrustState state) => new(state, Now.AddDays(-10), "v1");

    [Fact]
    public void EachState_IsIssuedWithItsRate_AndWhatSetIt()
    {
        var snapshot = PolicySnapshotIssuer.Issue("s-7", Now,
        [
            new SliceIssue("ml", "shadow", In(TrustState.Shadow)),
            new SliceIssue("ml", "active", In(TrustState.Active)) { OtherKnobRates = new Dictionary<string, double> { ["watch"] = 0.05 } },
            new SliceIssue("ml", "stable", In(TrustState.Stable), new BudgetGrant("stable", 0.04, 800, true, RateTerm.Miss)) { AbstainQuantile = 0.05 },
        ], Policy, sliceKeyVersion: "k2");

        Assert.Equal(Now.AddDays(1), snapshot.SoftExpiresAt);
        Assert.Equal(Now.AddDays(3), snapshot.HardExpiresAt);
        Assert.Equal("k2", snapshot.SliceKeyVersion);
        var entry = snapshot.Entries.ToDictionary(e => e.SliceKey);
        Assert.Null(entry["shadow"].SampleRates);
        Assert.Equal(0.3, entry["active"].SampleRates!["uniform"]);
        Assert.Equal(0.05, entry["active"].SampleRates!["watch"]);
        Assert.Equal(PolicySnapshotIssuer.ActiveRateTerm, entry["active"].RateSetBy);
        Assert.Equal(0.04, entry["stable"].SampleRates!["uniform"]);
        Assert.Equal(RateTerm.Miss, entry["stable"].RateSetBy);
        Assert.Equal(0.05, entry["stable"].AbstainQuantile);
    }

    [Fact]
    public void AnIssuedSnapshot_RoundTripsThroughItsJsonForm()
    {
        var snapshot = PolicySnapshotIssuer.Issue("s-7", Now,
            [new SliceIssue("ml", "stable", In(TrustState.Stable), new BudgetGrant("stable", 0.04, 800, false, RateTerm.Budget))], Policy);

        var back = PolicySnapshotJson.Deserialize(PolicySnapshotJson.Serialize(snapshot));

        Assert.Equal(RateTerm.Budget, back.Entries[0].RateSetBy);
        Assert.Equal(0.04, back.Entries[0].SampleRates!["uniform"]);
    }

    [Fact]
    public void AStableTierWithoutAGrant_IsRefused()
        => Assert.Throws<ArgumentException>(() => PolicySnapshotIssuer.Issue("s", Now, [new SliceIssue("ml", "x", In(TrustState.Stable))], Policy));

    [Fact]
    public void ThePromiseKnob_CannotBeSetByHand()
        => Assert.Throws<ArgumentException>(() => PolicySnapshotIssuer.Issue("s", Now,
            [new SliceIssue("ml", "x", In(TrustState.Active)) { OtherKnobRates = new Dictionary<string, double> { ["uniform"] = 0.9 } }], Policy));

    [Fact]
    public void ASoftLifeLongerThanTheHardLife_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PolicySnapshotIssuer.Issue("s", Now, [], Policy with { SoftLife = TimeSpan.FromDays(5) }));
}
