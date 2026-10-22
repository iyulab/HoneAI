using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// File-backed <see cref="IReviewStore"/>: one JSON line per event — a review opened, a review
/// decided — appended, never rewritten. The state is the file folded in order: the first open of an id
/// makes the review, the first decision of it stands. Reviews survive restarts, and two processes on one
/// machine may share the file (a worker opens, a web request decides).
/// </summary>
/// <remarks>
/// A reference implementation, as the JSONL provenance sink is: each call reads the whole file, so it
/// suits the review volumes of a model lifecycle (a few a day), not a queue of predictions. Operational
/// storage — a database table keyed by review id — implements <see cref="IReviewStore"/> itself.
/// </remarks>
public sealed class JsonlReviewStore : IReviewStore
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="path">The JSONL file (created on first write, with parent directories).</param>
    /// <param name="clock">Time source; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public JsonlReviewStore(string path, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<ReviewRecord> OpenAsync(string reviewId, string? subject = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reviews = await FoldAsync(cancellationToken).ConfigureAwait(false);
            if (reviews.TryGetValue(reviewId, out var existing))
                return existing;
            var opened = new ReviewEvent("opened", reviewId, _clock(), Subject: subject);
            await JsonlFile.AppendAsync(_path, opened, cancellationToken).ConfigureAwait(false);
            // Another process may have opened it between the read and the append; the fold says which open stands.
            return (await FoldAsync(cancellationToken).ConfigureAwait(false))[reviewId];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ReviewRecord?> DecideAsync(string reviewId, ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);
        ArgumentNullException.ThrowIfNull(decision);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reviews = await FoldAsync(cancellationToken).ConfigureAwait(false);
            if (!reviews.TryGetValue(reviewId, out var review))
                return null;
            if (review.IsDecided)
                return review;
            var decided = new ReviewEvent("decided", reviewId, _clock(), Decision: decision);
            await JsonlFile.AppendAsync(_path, decided, cancellationToken).ConfigureAwait(false);
            return (await FoldAsync(cancellationToken).ConfigureAwait(false))[reviewId];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ReviewRecord?> GetAsync(string reviewId, CancellationToken cancellationToken = default)
        => (await FoldAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(reviewId);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewRecord>> ListPendingAsync(CancellationToken cancellationToken = default)
        => [.. (await FoldAsync(cancellationToken).ConfigureAwait(false)).Values.Where(r => !r.IsDecided).OrderBy(r => r.OpenedAt)];

    private async Task<Dictionary<string, ReviewRecord>> FoldAsync(CancellationToken cancellationToken)
    {
        var reviews = new Dictionary<string, ReviewRecord>(StringComparer.Ordinal);
        await foreach (var e in JsonlFile.ReadAsync<ReviewEvent>(_path, cancellationToken).ConfigureAwait(false))
        {
            switch (e.Kind)
            {
                case "opened" when !reviews.ContainsKey(e.ReviewId):
                    reviews[e.ReviewId] = new ReviewRecord(e.ReviewId, e.Subject, e.At);
                    break;
                case "decided" when e.Decision is not null
                                    && reviews.TryGetValue(e.ReviewId, out var review) && !review.IsDecided:
                    reviews[e.ReviewId] = review with { Decision = e.Decision, DecidedAt = e.At };
                    break;
            }
        }
        return reviews;
    }

    private sealed record ReviewEvent(
        string Kind, string ReviewId, DateTimeOffset At, string? Subject = null, ReviewDecision? Decision = null);
}
