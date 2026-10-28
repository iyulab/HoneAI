using System;
using System.Collections.Generic;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class SnapshotExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static PolicySnapshot Snapshot() => new()
    {
        Version = "s-1",
        IssuedAt = Now,
        SoftExpiresAt = Now.AddDays(1),
        HardExpiresAt = Now.AddDays(3),
        Entries = new List<PolicyEntry>(),
    };

    private static StandingChange Step(TrustState from, TrustState to, bool awaitsApproval = false)
        => new(from, to, "test", awaitsApproval, new SliceStanding(to, Now, "v1"));

    [Fact]
    public void TheExpiries_AreReadAtOrAfterTheirTime()
    {
        var snapshot = Snapshot();

        Assert.False(snapshot.IsSoftExpired(snapshot.SoftExpiresAt.AddTicks(-1)));
        Assert.True(snapshot.IsSoftExpired(snapshot.SoftExpiresAt));
        Assert.False(snapshot.IsHardExpired(snapshot.HardExpiresAt.AddTicks(-1)));
        Assert.True(snapshot.IsHardExpired(snapshot.HardExpiresAt));
    }

    [Theory]
    [InlineData(TrustState.Shadow, TrustState.Active)]
    [InlineData(TrustState.Active, TrustState.Stable)]
    [InlineData(TrustState.Shadow, TrustState.Stable)]
    public void AReleaseAwaitingApproval_IsApplied_OnlyBeforeTheSoftExpiry(TrustState from, TrustState to)
    {
        var snapshot = Snapshot();
        var release = Step(from, to, awaitsApproval: true);

        Assert.True(SnapshotExpiry.GainsGround(release));
        Assert.True(SnapshotExpiry.MayApply(snapshot, release, snapshot.SoftExpiresAt.AddTicks(-1)));
        Assert.False(SnapshotExpiry.MayApply(snapshot, release, snapshot.SoftExpiresAt));
        Assert.False(SnapshotExpiry.MayApply(snapshot, release, snapshot.HardExpiresAt.AddDays(5)));
    }

    [Theory]
    [InlineData(TrustState.Stable, TrustState.Active)]
    [InlineData(TrustState.Active, TrustState.Shadow)]
    [InlineData(TrustState.Stable, TrustState.Shadow)]
    [InlineData(TrustState.Active, TrustState.Active)]
    [InlineData(TrustState.Shadow, TrustState.Shadow)]
    public void AStepThatKeepsOrLosesGround_IsNeverHeldBackByAnExpiry(TrustState from, TrustState to)
    {
        var snapshot = Snapshot();
        var step = Step(from, to);

        Assert.False(SnapshotExpiry.GainsGround(step));
        Assert.True(SnapshotExpiry.MayApply(snapshot, step, snapshot.HardExpiresAt.AddDays(30)));
    }
}
