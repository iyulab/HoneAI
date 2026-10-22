namespace HoneAI;

/// <summary>
/// A human review decision on a gated prediction or lifecycle run. The canonical
/// <see cref="Verdict"/> tokens are <c>approve</c> | <c>reject</c> | <c>retrain</c>,
/// but the field is free-form so a consumer can carry its own vocabulary.
/// </summary>
/// <param name="Verdict">The reviewer's decision (e.g. "approve", "reject", "retrain").</param>
/// <param name="Comment">Optional reviewer comment.</param>
/// <param name="ReviewerKey">
/// A pseudonymous key for the person who decided, so trust can be measured per reviewer. Never a
/// name: mapping keys to people, retention and consent are the consumer's, and per-person numbers
/// are for weighting labels, not for evaluating people.
/// </param>
public sealed record ReviewDecision(string Verdict, string? Comment = null, string? ReviewerKey = null);

/// <summary>A review as stored: opened for a subject, and decided once.</summary>
/// <param name="ReviewId">The id the review is keyed by; one id is one decision.</param>
/// <param name="Subject">What is under review, in the consumer's words (a run id, an experiment).</param>
/// <param name="OpenedAt">When it was opened.</param>
/// <param name="Decision">The decision, or <see langword="null"/> while it waits.</param>
/// <param name="DecidedAt">When it was decided.</param>
public sealed record ReviewRecord(
    string ReviewId,
    string? Subject,
    DateTimeOffset OpenedAt,
    ReviewDecision? Decision = null,
    DateTimeOffset? DecidedAt = null)
{
    /// <summary>Whether a decision has been recorded.</summary>
    public bool IsDecided => Decision is not null;
}

/// <summary>
/// Human review as stored state: a producer opens a review, a reviewer decides it — possibly in
/// another process, hours or days later — and anyone reads it. Nothing waits in memory, so a review
/// outlives a restart and crosses a process boundary (a queue worker opens it, a web request decides).
/// </summary>
/// <remarks>
/// The contract is HoneAI's and the storage the consumer's: operational stores (a database table)
/// implement this; <c>HoneAI.Core</c> carries in-memory and JSONL reference implementations.
/// Both operations are idempotent by review id — opening an open review returns it, and the first
/// decision stands — so a retried request never makes a second review or a second decision.
/// </remarks>
public interface IReviewStore
{
    /// <summary>
    /// Opens a review for <paramref name="reviewId"/>, or returns the one already stored under it
    /// (decided or not) unchanged.
    /// </summary>
    Task<ReviewRecord> OpenAsync(string reviewId, string? subject = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records <paramref name="decision"/> on an open review and returns the stored record. A review
    /// already decided keeps its first decision — the returned record shows which one stands.
    /// <see langword="null"/> when no review is stored under <paramref name="reviewId"/>.
    /// </summary>
    Task<ReviewRecord?> DecideAsync(string reviewId, ReviewDecision decision, CancellationToken cancellationToken = default);

    /// <summary>The review stored under <paramref name="reviewId"/>, or <see langword="null"/>.</summary>
    Task<ReviewRecord?> GetAsync(string reviewId, CancellationToken cancellationToken = default);

    /// <summary>The reviews waiting for a decision, oldest first — what a review screen lists.</summary>
    Task<IReadOnlyList<ReviewRecord>> ListPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>Conveniences over <see cref="IReviewStore"/>.</summary>
public static class ReviewStoreExtensions
{
    /// <summary>
    /// Opens the review (when not yet open) and waits for its decision by reading the store every
    /// <paramref name="pollInterval"/> — for a caller that holds a process for the review anyway.
    /// A durable caller does not wait: it opens, returns, and resumes when the decision is stored.
    /// </summary>
    public static async Task<ReviewDecision> WaitForDecisionAsync(
        this IReviewStore store, string reviewId, TimeSpan pollInterval, string? subject = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var review = await store.OpenAsync(reviewId, subject, cancellationToken).ConfigureAwait(false);
        while (review.Decision is null)
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            review = await store.GetAsync(reviewId, cancellationToken).ConfigureAwait(false) ?? review;
        }
        return review.Decision;
    }
}
