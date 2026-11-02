using System;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class SeatDecisionRecordsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static SeatDecision Decision(SeatReview review, bool hard = false, bool blind = true, double probability = 0.25) => new()
    {
        Role = AgentRole.Orchestrator,
        Task = "tabular",
        SeatVersion = "v3",
        DecisionId = "d1",
        DecidedAt = At,
        Review = review,
        ReviewProbability = probability,
        ReviewedBlind = blind,
        HardToReverse = hard,
    };

    [Fact]
    public void TheSeatIsTheTier_TheTaskTheSlice_TheSeatsVersionTheTiersVersion()
    {
        var record = SeatDecisionRecords.From(Decision(SeatReview.Approved));

        Assert.Equal("seat:Orchestrator", record.TierId);
        Assert.Equal("seat:Orchestrator", SeatDecisionRecords.TierOf(AgentRole.Orchestrator));
        Assert.Equal("tabular", record.SliceKey);
        Assert.Equal("v3", record.SnapshotVersion);
        Assert.Equal("d1", record.DecisionId);
        Assert.Equal(At, record.DecidedAt);
        Assert.Equal(0.25, record.Probability);
        Assert.Equal("person", record.CheckedBy);
    }

    [Theory]
    [InlineData(SeatReview.Approved, RecheckOutcome.Agreed)]
    [InlineData(SeatReview.Corrected, RecheckOutcome.Disagreed)]
    [InlineData(SeatReview.Rejected, RecheckOutcome.Disagreed)]
    [InlineData(SeatReview.Unanswered, RecheckOutcome.UpperFailed)]
    public void AReviewIsAnAgreementADisagreementOrAFailureOfTheTierAbove(SeatReview review, RecheckOutcome expected)
        => Assert.Equal(expected, SeatDecisionRecords.From(Decision(review)).Outcome);

    [Theory]
    [InlineData(SeatReview.Rejected, true, true)]
    [InlineData(SeatReview.Rejected, false, false)]      // a rejection of a decision that could be undone is a disagreement, not a must-catch
    [InlineData(SeatReview.Corrected, true, false)]
    [InlineData(SeatReview.Approved, true, false)]
    public void OnlyARejectionOfADecisionThatCouldNotBeUndone_IsAMustCatch(SeatReview review, bool hard, bool expected)
        => Assert.Equal(expected, SeatDecisionRecords.From(Decision(review, hard: hard)).UpperMustCatch);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhetherThePersonSawTheProposal_IsCarriedAsBlind(bool blind)
        => Assert.Equal(blind, SeatDecisionRecords.From(Decision(SeatReview.Approved, blind: blind)).Blind);

    [Fact]
    public void AReviewIsBlindUnlessSaidOtherwise()
        => Assert.True(new SeatDecision
        {
            Role = AgentRole.Translator,
            Task = "t",
            SeatVersion = "v",
            DecisionId = "d",
            DecidedAt = At,
            Review = SeatReview.Approved,
            ReviewProbability = 0.5,
        }.ReviewedBlind);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void AProbabilityOutsideZeroToOne_IsRefused(double probability)
        => Assert.Throws<ArgumentOutOfRangeException>(() => SeatDecisionRecords.From(Decision(SeatReview.Approved, probability: probability)));

    [Fact]
    public void ADecisionDrawnForSureIsAllowed()
        => Assert.Equal(1.0, SeatDecisionRecords.From(Decision(SeatReview.Approved, probability: 1.0)).Probability);

    [Fact]
    public void ABlankTaskOrIdIsRefused()
    {
        Assert.Throws<ArgumentException>(() => SeatDecisionRecords.From(Decision(SeatReview.Approved) with { Task = " " }));
        Assert.Throws<ArgumentException>(() => SeatDecisionRecords.From(Decision(SeatReview.Approved) with { DecisionId = "" }));
    }
}
