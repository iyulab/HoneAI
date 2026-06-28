namespace HoneAI;

/// <summary>
/// A human review decision on a gated prediction or lifecycle run. The canonical
/// <see cref="Verdict"/> tokens are <c>approve</c> | <c>reject</c> | <c>retrain</c>
/// (SMI.AIMS), but the field is free-form so a consumer can carry its own vocabulary.
/// </summary>
/// <param name="Verdict">The reviewer's decision (e.g. "approve", "reject", "retrain").</param>
/// <param name="Comment">Optional reviewer comment.</param>
public sealed record ReviewDecision(string Verdict, string? Comment = null);

/// <summary>
/// Async human-in-the-loop gate: a producer opens a gate for an item and awaits the
/// human <see cref="ReviewDecision"/>; a reviewer submits the decision to release it.
/// Generalizes SMI.AIMS's <c>SubmitReview</c> (a <c>TaskCompletionSource</c> gate keyed
/// by run id) and U-Vision's review queue + <c>requires_review</c> (back-derivation
/// §3.5 ③). The review UI and approval policy stay in the consumer adapter.
/// </summary>
public interface IHitlGate
{
    /// <summary>
    /// Open a review gate for <paramref name="reviewId"/> and await the human decision.
    /// The returned task completes when <see cref="Submit"/> is called for the same id,
    /// or faults/cancels via <paramref name="cancellationToken"/>.
    /// </summary>
    Task<ReviewDecision> AwaitDecisionAsync(string reviewId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submit a decision, releasing any waiter on <paramref name="reviewId"/>.
    /// Returns <see langword="false"/> when no gate is open for that id.
    /// </summary>
    bool Submit(string reviewId, ReviewDecision decision);
}
