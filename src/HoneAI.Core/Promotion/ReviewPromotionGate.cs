using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// A person's review decides: the gate opens a review for the candidate in an <see cref="IReviewStore"/> — once, a
/// resumed run finds the one it opened — and promotes on an approval, refuses on anything else, and waits while the
/// review is undecided.
/// </summary>
public sealed class ReviewPromotionGate : IPromotionGate
{
    private static readonly HashSet<string> ApprovalVerdicts =
        new(StringComparer.OrdinalIgnoreCase) { "approve", "approved", "accept", "promote" };

    private readonly IReviewStore _reviews;

    /// <param name="reviews">Where the review is opened and its decision read.</param>
    public ReviewPromotionGate(IReviewStore reviews)
        => _reviews = reviews ?? throw new ArgumentNullException(nameof(reviews));

    /// <summary>Whether <paramref name="verdict"/> approves: <c>approve</c>, <c>approved</c>, <c>accept</c> or <c>promote</c>, ignoring case.</summary>
    public static bool Approves(string verdict) => ApprovalVerdicts.Contains(verdict);

    /// <inheritdoc />
    public async Task<PromotionVerdict> JudgeAsync(PromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var review = await _reviews.OpenAsync(candidate.ReviewId ?? candidate.RunId, candidate.RunId, cancellationToken).ConfigureAwait(false);
        if (review.Decision is not { } decision)
            return new(PromotionDecision.Wait, "the review is not decided", JudgeKind.Human);
        return new(Approves(decision.Verdict) ? PromotionDecision.Promote : PromotionDecision.Refuse, decision.Comment, JudgeKind.Human);
    }
}
