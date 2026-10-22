using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// <see cref="IReviewStore"/> in this process's memory — for tests and for a single process whose
/// reviews need not outlive it. Reviews that must survive a restart or cross a process boundary go to
/// <see cref="JsonlReviewStore"/> or the consumer's own store.
/// </summary>
public sealed class InMemoryReviewStore : IReviewStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ReviewRecord> _reviews = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="clock">Time source for opening and deciding; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryReviewStore(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public Task<ReviewRecord> OpenAsync(string reviewId, string? subject = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);
        lock (_gate)
        {
            if (!_reviews.TryGetValue(reviewId, out var review))
                _reviews[reviewId] = review = new ReviewRecord(reviewId, subject, _clock());
            return Task.FromResult(review);
        }
    }

    /// <inheritdoc />
    public Task<ReviewRecord?> DecideAsync(string reviewId, ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate)
        {
            if (!_reviews.TryGetValue(reviewId, out var review))
                return Task.FromResult<ReviewRecord?>(null);
            if (!review.IsDecided)
                _reviews[reviewId] = review = review with { Decision = decision, DecidedAt = _clock() };
            return Task.FromResult<ReviewRecord?>(review);
        }
    }

    /// <inheritdoc />
    public Task<ReviewRecord?> GetAsync(string reviewId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_reviews.TryGetValue(reviewId, out var review) ? review : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReviewRecord>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ReviewRecord> pending = [.. _reviews.Values.Where(r => !r.IsDecided).OrderBy(r => r.OpenedAt)];
            return Task.FromResult(pending);
        }
    }
}
