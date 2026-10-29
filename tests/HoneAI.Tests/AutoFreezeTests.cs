using System;
using System.Linq;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class AutoFreezeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly AutoFreezePolicy Policy = new(ConsecutiveRollbacks: 2, VersionChanges: 3, Within: TimeSpan.FromDays(7), MinimumRate: 0.2);

    private static StandingEvent At(double daysAgo, StandingEventKind kind) => new(Now.AddDays(-daysAgo), kind);

    [Fact]
    public void NoEvents_NoHold() => Assert.Null(AutoFreeze.Decide([], Policy, Now));

    [Fact]
    public void TwoRollbacksWithNoReleaseBetween_PlaceAHoldThatKeepsTheMinimumRate()
    {
        var hold = AutoFreeze.Decide([At(5, StandingEventKind.RolledBack), At(1, StandingEventKind.RolledBack)], Policy, Now);

        Assert.NotNull(hold);
        Assert.Equal(AutoFreeze.By, hold.By);
        Assert.Equal(Now, hold.At);
        Assert.Equal(0.2, hold.MinimumRate);
        Assert.Contains("2 rollbacks", hold.Reason);
        Assert.Contains("7 days", hold.Reason);
    }

    [Fact]
    public void AReleaseBetweenTwoRollbacks_BreaksTheRun()
    {
        var hold = AutoFreeze.Decide([At(5, StandingEventKind.RolledBack), At(3, StandingEventKind.Released), At(1, StandingEventKind.RolledBack)], Policy, Now);

        Assert.Null(hold);
    }

    [Fact]
    public void RollbacksOutsideTheTimeWindow_AreNotCounted()
    {
        Assert.Null(AutoFreeze.Decide([At(20, StandingEventKind.RolledBack), At(1, StandingEventKind.RolledBack)], Policy, Now));
    }

    [Fact]
    public void ThreeVersionChangesInTheWindow_PlaceAHold_TwoDoNot()
    {
        var two = new[] { At(6, StandingEventKind.VersionChanged), At(2, StandingEventKind.VersionChanged) };
        var three = two.Append(At(1, StandingEventKind.VersionChanged)).ToList();

        Assert.Null(AutoFreeze.Decide(two, Policy, Now));
        var hold = AutoFreeze.Decide(three, Policy, Now);
        Assert.NotNull(hold);
        Assert.Contains("version changed 3 times", hold.Reason);
    }

    [Fact]
    public void AReasonSetToZero_IsOff()
    {
        var off = Policy with { ConsecutiveRollbacks = 0 };

        Assert.Null(AutoFreeze.Decide([At(2, StandingEventKind.RolledBack), At(1, StandingEventKind.RolledBack), At(0.5, StandingEventKind.RolledBack)], off, Now));
    }

    [Fact]
    public void AnEventAfterNow_IsNotYetHappened()
    {
        Assert.Null(AutoFreeze.Decide([At(5, StandingEventKind.RolledBack), new StandingEvent(Now.AddHours(1), StandingEventKind.RolledBack)], Policy, Now));
    }

    [Theory]
    [InlineData(-1, 0, 7)]
    [InlineData(0, -1, 7)]
    [InlineData(1, 1, 0)]
    public void ARefusedPolicy_Throws(int rollbacks, int versions, int days)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AutoFreeze.Decide([], new AutoFreezePolicy(rollbacks, versions, TimeSpan.FromDays(days)), Now));

    [Fact]
    public void ARateOutsideZeroToOne_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AutoFreeze.Decide([], Policy with { MinimumRate = 1.5 }, Now));
}
