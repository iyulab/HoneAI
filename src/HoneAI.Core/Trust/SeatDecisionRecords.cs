using System;

namespace HoneAI.Trust;

/// <summary>How a person answered a decision a seat made.</summary>
public enum SeatReview
{
    /// <summary>The person agreed with the decision as it was.</summary>
    Approved = 0,

    /// <summary>The person changed the decision.</summary>
    Corrected = 1,

    /// <summary>The person turned the decision down.</summary>
    Rejected = 2,

    /// <summary>The decision was drawn for review and nobody answered — a failure of the upper tier, not an agreement.</summary>
    Unanswered = 3,
}

/// <summary>
/// What a consumer keeps of one decision an agent made in a seat — a role it plays in place of an expert (<see cref="AgentRole"/>) — and
/// of the review a person gave it.
/// </summary>
public sealed record SeatDecision
{
    /// <summary>The role the agent played.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>The task the decision was about — the slice the seat is measured in.</summary>
    public required string Task { get; init; }

    /// <summary>The version of the seat (persona, knowledge, tool set) that decided. A new version starts over in shadow.</summary>
    public required string SeatVersion { get; init; }

    /// <summary>The decision.</summary>
    public required string DecisionId { get; init; }

    /// <summary>When the seat decided.</summary>
    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>What the person did.</summary>
    public required SeatReview Review { get; init; }

    /// <summary>
    /// The probability with which the decision was drawn for review — chosen and recorded <em>before</em> the person saw the proposal
    /// (in (0, 1]).
    /// </summary>
    public required double ReviewProbability { get; init; }

    /// <summary>
    /// The person decided before seeing what the seat proposed. Approving a proposal in view is not evidence of what the seat decides
    /// alone, so the ledger counts only the blind ones. Defaults to true: a review is blind unless said otherwise.
    /// </summary>
    public bool ReviewedBlind { get; init; } = true;

    /// <summary>The decision cannot be taken back, or is costly to: one the person rejects is a must-catch the seat should have only proposed.</summary>
    public bool HardToReverse { get; init; }

    /// <summary>
    /// The kind of checker that reviewed, never an individual: a person's approvals are not totalled per person (the record names
    /// a tier or a kind of checker and nothing finer).
    /// </summary>
    public string CheckedBy { get; init; } = "person";
}

/// <summary>
/// A seat's decisions as the records the trust ledger and the lifecycle already read: the seat is the tier, the task is the slice, the
/// seat's version is the tier's version, the person is the tier above that rechecks. Nothing about the ledger changes for a seat.
/// </summary>
public static class SeatDecisionRecords
{
    /// <summary>The tier id a seat is measured as.</summary>
    public static string TierOf(AgentRole role) => $"seat:{role}";

    /// <summary>The ledger record of <paramref name="decision"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The review probability is not in (0, 1].</exception>
    public static RecheckRecord From(SeatDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Task);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.DecisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.CheckedBy);
        if (!(decision.ReviewProbability > 0 && decision.ReviewProbability <= 1))
            throw new ArgumentOutOfRangeException(nameof(decision), decision.ReviewProbability, "The probability a decision was drawn for review with is in (0, 1].");

        return new RecheckRecord
        {
            DecisionId = decision.DecisionId,
            TierId = TierOf(decision.Role),
            SliceKey = decision.Task,
            DecidedAt = decision.DecidedAt,
            Probability = decision.ReviewProbability,
            SnapshotVersion = decision.SeatVersion,
            Outcome = decision.Review switch
            {
                SeatReview.Approved => RecheckOutcome.Agreed,
                SeatReview.Unanswered => RecheckOutcome.UpperFailed,
                _ => RecheckOutcome.Disagreed,
            },
            // A decision that cannot be undone, which the person says the seat should have proposed instead of carrying out.
            UpperMustCatch = decision.HardToReverse && decision.Review == SeatReview.Rejected,
            CheckedBy = decision.CheckedBy,
            Blind = decision.ReviewedBlind,
        };
    }
}
