using System;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>Behaviour tests for the production <see cref="InMemoryHitlGate"/>.</summary>
public class InMemoryHitlGateTests
{
    [Fact]
    public async Task Submit_ReleasesWaiterWithDecision()
    {
        var gate = new InMemoryHitlGate();
        var waiter = gate.AwaitDecisionAsync("review-42");

        Assert.Equal(1, gate.PendingCount);
        Assert.True(gate.Submit("review-42", new ReviewDecision("approve", "ship it")));

        var decision = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("approve", decision.Verdict);
        Assert.Equal("ship it", decision.Comment);
        Assert.Equal(0, gate.PendingCount);
    }

    [Fact]
    public void Submit_WithoutWaiter_ReturnsFalse()
    {
        var gate = new InMemoryHitlGate();
        Assert.False(gate.Submit("nobody-waiting", new ReviewDecision("approve")));
    }

    [Fact]
    public void AwaitDecision_DuplicateOpenId_Throws()
    {
        var gate = new InMemoryHitlGate();
        _ = gate.AwaitDecisionAsync("dup");

        // The guard throws synchronously (before returning the Task), so a void-returning
        // action is the correct assertion shape here.
        Assert.Throws<InvalidOperationException>(() => { _ = gate.AwaitDecisionAsync("dup"); });
    }

    [Fact]
    public async Task AwaitDecision_Cancellation_RemovesGateAndCancelsWaiter()
    {
        var gate = new InMemoryHitlGate();
        using var cts = new CancellationTokenSource();

        var waiter = gate.AwaitDecisionAsync("cancel-me", cts.Token);
        Assert.Equal(1, gate.PendingCount);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, gate.PendingCount);

        // The id is free again after cancellation.
        var second = gate.AwaitDecisionAsync("cancel-me");
        Assert.True(gate.Submit("cancel-me", new ReviewDecision("approve")));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IndependentGates_ReleaseIndependently()
    {
        var gate = new InMemoryHitlGate();
        var a = gate.AwaitDecisionAsync("a");
        var b = gate.AwaitDecisionAsync("b");

        gate.Submit("b", new ReviewDecision("reject"));

        var bDecision = await b.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("reject", bDecision.Verdict);
        Assert.False(a.IsCompleted);            // 'a' still pending
        Assert.Equal(1, gate.PendingCount);
    }
}
