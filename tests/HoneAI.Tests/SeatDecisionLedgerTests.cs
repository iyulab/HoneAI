using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// A role an agent plays in place of an expert (a seat) makes decisions about a system rather than answers about an input, and a
/// person reviews some of them. Can the trust ledger and the lifecycle that measure a tier read those reviews as they are? A seat's
/// decision is recorded as a recheck — the seat as the tier, the task as the slice, the person's approval as agreement — and the
/// tests below say what then holds.
/// </summary>
public class SeatDecisionLedgerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(7);

    private enum Review { Approved, Corrected, Rejected, Unanswered }

    /// <summary>What a consumer records of one seat decision.</summary>
    private sealed record SeatDecision(
        AgentRole Role, string Task, string SeatVersion, string Id, DateTimeOffset At,
        Review Result, double ReviewProbability, bool ReviewedBlind = true, bool HardToReverse = false);

    private static string TierOf(AgentRole role) => $"seat:{role}";

    /// <summary>The mapping under test: seat → tier, task → slice, approval → agreement.</summary>
    private static RecheckRecord ToRecord(SeatDecision d) => new()
    {
        DecisionId = d.Id,
        TierId = TierOf(d.Role),
        SliceKey = d.Task,
        DecidedAt = d.At,
        Probability = d.ReviewProbability,
        SnapshotVersion = d.SeatVersion,
        Outcome = d.Result switch
        {
            Review.Approved => RecheckOutcome.Agreed,
            Review.Unanswered => RecheckOutcome.UpperFailed,
            _ => RecheckOutcome.Disagreed,
        },
        // A decision that cannot be undone, which the person says the seat should have proposed instead of carrying out.
        UpperMustCatch = d.HardToReverse && d.Result == Review.Rejected,
        CheckedBy = "person",
        Blind = d.ReviewedBlind,
    };

    private static IEnumerable<SeatDecision> Decisions(
        AgentRole role, string task, int count, int notApproved, string version = "v1", double probability = 0.5, bool blind = true)
        => Enumerable.Range(0, count).Select(i => new SeatDecision(
            role, task, version, $"{role}-{task}-{version}-{i}", Start.AddMinutes(i * 20), i < notApproved ? Review.Corrected : Review.Approved,
            probability, blind));

    private static readonly LifecyclePolicy Policy = new()
    {
        Targets = new TrustTargets
        {
            Agreement = 0.85, DetectableDrop = 0.05, MissLimit = 0.05, Deadline = TimeSpan.FromDays(7),
            // Decisions that cannot be undone are proposals, never carried out alone, so none is seen to slip through; the
            // allowance states how many a window may hide.
            HiddenMustCatchLimit = 8,
        },
        Margin = 0.02,
        MinimumEffectiveSample = 100,
        MinimumDwell = TimeSpan.FromDays(7),
        StableWindows = 2,
    };

    private static LedgerCell CellOf(IEnumerable<SeatDecision> decisions, AgentRole role, string task, int caught = 20, double confidence = 0.95)
        => TrustLedger.Compute(decisions.Select(ToRecord), TierOf(role), task, Start, End, caught, confidence);

    [Fact]
    public void ReviewsOfASeat_FillACellPerSeatAndPerTask_NotOneForAll()
    {
        var records = Decisions(AgentRole.Translator, "tabular", 400, 20)
            .Concat(Decisions(AgentRole.Translator, "vision", 400, 200))
            .Concat(Decisions(AgentRole.Operator, "tabular", 400, 100))
            .ToList();

        var translatorTabular = CellOf(records, AgentRole.Translator, "tabular");
        var translatorVision = CellOf(records, AgentRole.Translator, "vision");
        var operatorTabular = CellOf(records, AgentRole.Operator, "tabular");

        Assert.Equal(0.95, translatorTabular.Agreement!.Value, 3);
        Assert.Equal(0.5, translatorVision.Agreement!.Value, 3);
        Assert.Equal(0.75, operatorTabular.Agreement!.Value, 3);
        Assert.All(new[] { translatorTabular, translatorVision, operatorTabular }, c => Assert.Equal(400, c.Rechecks));
    }

    [Fact]
    public void AnUnansweredReview_IsNotDropped_ItCountsAgainstTheSeat()
    {
        var records = Decisions(AgentRole.Translator, "tabular", 200, 0)
            .Select(d => d.Id.EndsWith("-0") || d.Id.EndsWith("-1") || d.Id.EndsWith("-2") ? d with { Result = Review.Unanswered } : d)
            .ToList();

        var cell = CellOf(records, AgentRole.Translator, "tabular");

        Assert.True(cell.UpperFailures > 0);
        Assert.True(cell.AgreementLower < CellOf(Decisions(AgentRole.Translator, "tabular", 200, 0), AgentRole.Translator, "tabular").AgreementLower);
    }

    [Fact]
    public void AReviewedSeat_ReleasesFromShadowToActive_PendingApproval()
    {
        var cell = CellOf(Decisions(AgentRole.Translator, "tabular", 400, 20), AgentRole.Translator, "tabular");

        var step = TrustLifecycle.Release(SliceStanding.New("v1", Start), cell, teacherQualified: true, new LiveSignals("v1"), Policy, End);

        Assert.Equal(TrustState.Active, step.To);
        Assert.True(step.AwaitsApproval);
    }

    [Fact]
    public void ASeatApprovedAtRandomlyChosenOddsOnly_ReleasesNoSooner_ThanTheEvidenceStands()
    {
        // Few reviews: the interval is wide, the lifecycle waits.
        var cell = CellOf(Decisions(AgentRole.Translator, "tabular", 30, 1), AgentRole.Translator, "tabular");

        var step = TrustLifecycle.Release(SliceStanding.New("v1", Start), cell, true, new LiveSignals("v1"), Policy, End);

        Assert.False(step.Moved);
    }

    [Fact]
    public void ApprovalsGivenWithTheProposalInView_AreNotEvidence_OfWhatTheSeatDecidesAlone()
    {
        // 400 approvals, every one given by a person who saw what the seat proposed. They agree with it more than they would have
        // decided alone, so the ledger does not count them: with only these, a seat is not released.
        var shown = Decisions(AgentRole.Translator, "tabular", 400, 0, blind: false).ToList();
        var cell = CellOf(shown, AgentRole.Translator, "tabular");
        Assert.Equal(0, cell.Rechecks);

        var step = TrustLifecycle.Release(SliceStanding.New("v1", Start), cell, true, new LiveSignals("v1"), Policy, End);
        Assert.False(step.Moved);

        // The same seat with a share of its reviews decided before the person saw the proposal is measured on those.
        var mixed = shown.Take(300).Concat(Decisions(AgentRole.Translator, "tabular", 120, 6, version: "v1", blind: true).Select(d => d with { Id = "b-" + d.Id }));
        var measured = CellOf(mixed, AgentRole.Translator, "tabular");
        Assert.Equal(120, measured.Rechecks);
    }

    [Fact]
    public void ARunOfCorrections_TightensAnActiveSeatBackToShadow()
    {
        var standing = new SliceStanding(TrustState.Active, Start, "v1");
        var running = CellOf(Decisions(AgentRole.Translator, "tabular", 400, 200), AgentRole.Translator, "tabular", confidence: 0.99);

        var step = TrustLifecycle.Tighten(standing, running, new LiveSignals("v1"), Policy, End);

        Assert.Equal(TrustState.Shadow, step.To);
        Assert.False(step.AwaitsApproval);   // tightening never waits
    }

    [Fact]
    public void ANewSeatVersion_StartsOverInShadow_WhateverItsPredecessorEarned()
    {
        var stable = new SliceStanding(TrustState.Stable, Start, "v1");
        var cell = CellOf(Decisions(AgentRole.Translator, "tabular", 400, 10), AgentRole.Translator, "tabular");

        var step = TrustLifecycle.Tighten(stable, cell, new LiveSignals("v2"), Policy, End);

        Assert.Equal(TrustState.Shadow, step.To);
        Assert.Equal("v2", step.Standing.TierVersion);
    }

    [Fact]
    public void ARejectedDecisionThatCouldNotBeUndone_IsAMissInTheCell()
    {
        var records = Decisions(AgentRole.Orchestrator, "tabular", 200, 0).ToList();
        records[10] = records[10] with { Result = Review.Rejected, HardToReverse = true };

        var cell = CellOf(records, AgentRole.Orchestrator, "tabular", caught: 5);

        Assert.True(cell.MissesEstimated > 0);
    }

    [Fact]
    public void TheRecordNamesNoPerson_SoNoPersonsApprovalsCanBeTotalledFromIt()
    {
        var names = typeof(RecheckRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("User", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Person", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Reviewer", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Human", StringComparison.OrdinalIgnoreCase));
        // What checked is a tier or a kind of checker, never an individual.
        Assert.Equal(typeof(string), typeof(RecheckRecord).GetProperty(nameof(RecheckRecord.CheckedBy))!.PropertyType);
    }
}
