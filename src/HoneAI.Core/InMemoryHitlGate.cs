using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// In-process <see cref="IHitlGate"/> backed by a <see cref="TaskCompletionSource{TResult}"/>
/// per open review — the generic form of SMI.AIMS's <c>_reviewGates</c> (back-derivation
/// §3.5 ③). A producer opens a gate and awaits; a reviewer <see cref="Submit"/>s to release it.
/// </summary>
/// <remarks>
/// Single-process only (the waiter and the submitter share this instance). A cancelled
/// wait removes its gate so no entry leaks; opening a second gate for an id already open
/// throws, since a review id must identify exactly one pending decision.
/// </remarks>
public sealed class InMemoryHitlGate : IHitlGate
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReviewDecision>> _gates = new();

    /// <summary>Number of reviews currently awaiting a decision.</summary>
    public int PendingCount => _gates.Count;

    /// <inheritdoc />
    public Task<ReviewDecision> AwaitDecisionAsync(string reviewId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);

        var tcs = new TaskCompletionSource<ReviewDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_gates.TryAdd(reviewId, tcs))
            throw new InvalidOperationException($"A review gate is already open for '{reviewId}'.");

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() =>
            {
                if (_gates.TryRemove(reviewId, out var pending))
                    pending.TrySetCanceled(cancellationToken);
            });
            // Drop the registration once the gate completes (success or cancel).
            tcs.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration, TaskScheduler.Default);
        }

        return tcs.Task;
    }

    /// <inheritdoc />
    public bool Submit(string reviewId, ReviewDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);
        ArgumentNullException.ThrowIfNull(decision);

        if (_gates.TryRemove(reviewId, out var tcs))
            return tcs.TrySetResult(decision);
        return false;
    }
}
