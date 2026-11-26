using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>The miss rate when the tier above judges what the tier sent up only by sample too.</summary>
public class SampledCaughtTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(7);

    private static RecheckRecord Settled(int i, double pi, bool mustCatch = false, RecheckOutcome outcome = RecheckOutcome.Agreed, bool blind = true) => new()
    {
        DecisionId = $"s{i}",
        TierId = "ml",
        SliceKey = "a",
        DecidedAt = Start.AddMinutes(i),
        Probability = pi,
        Outcome = outcome,
        UpperMustCatch = mustCatch,
        Blind = blind,
    };

    private static SentUpRecheck Up(int i, double pi, bool mustCatch = true, bool failed = false, bool blind = true) => new()
    {
        DecisionId = $"u{i}",
        TierId = "ml",
        SliceKey = "a",
        DecidedAt = Start.AddMinutes(i),
        Probability = pi,
        UpperMustCatch = mustCatch,
        UpperFailed = failed,
        Blind = blind,
    };

    private static LedgerCell Cell(IEnumerable<RecheckRecord> records, IEnumerable<SentUpRecheck>? sentUp = null, int caught = 0)
        => TrustLedger.Compute(records, "ml", "a", Start, End, caught, sentUp: sentUp);

    [Fact]
    public void OneMissAndThreeCaughtSeenAtOneRate_IsAQuarter_NotTheCountMixedWithAWeight()
    {
        var records = new[] { Settled(0, 0.2, mustCatch: true, RecheckOutcome.Disagreed), Settled(1, 0.2) };
        var cell = Cell(records, [Up(0, 0.2), Up(1, 0.2), Up(2, 0.2)]);

        Assert.Equal(0.25, cell.MissRate!.Value, 10);
        Assert.Equal(5, cell.MissesEstimated, 10);
        Assert.Equal(15, cell.Caught);
        Assert.Equal(3, cell.SentUpRechecks);
        Assert.Equal(0.625, Cell(records, caught: 3).MissRate!.Value, 10);   // what the count path gives for the same numbers
    }

    [Fact]
    public void NoMissAmongFourSeen_HasTheExactBinomialUpperBound()
    {
        var cell = Cell([Settled(0, 0.2)], Enumerable.Range(0, 4).Select(i => Up(i, 0.2)));

        Assert.Equal(0, cell.MissRate);
        Assert.Equal(0.527, cell.MissRateUpper!.Value, 3);
        Assert.Equal(0, cell.MissRateLower);
    }

    [Fact]
    public void AWindowJudgedWhole_GivesTheSameRateAndBoundsAsACount()
    {
        var records = new[] { Settled(0, 1, mustCatch: true, RecheckOutcome.Disagreed), Settled(1, 1), Settled(2, 1, mustCatch: true, RecheckOutcome.Disagreed) };
        var counted = Cell(records, caught: 6);
        var sampled = Cell(records, Enumerable.Range(0, 6).Select(i => Up(i, 1)));

        Assert.Equal(counted.MissRate!.Value, sampled.MissRate!.Value, 10);
        Assert.Equal(counted.MissRateUpper!.Value, sampled.MissRateUpper!.Value, 10);
        Assert.Equal(counted.MissRateLower!.Value, sampled.MissRateLower!.Value, 10);
        Assert.Equal(counted.Agreement, sampled.Agreement);
    }

    [Fact]
    public void FailedJudgmentsWidenTheBoundOnTheirOwnSide()
    {
        var records = new[] { Settled(0, 0.5, mustCatch: true, RecheckOutcome.Disagreed) };
        var plain = Cell(records, [Up(0, 0.5), Up(1, 0.5), Up(2, 0.5)]);
        var settledFailed = Cell([.. records, Settled(1, 0.5, outcome: RecheckOutcome.UpperFailed)], [Up(0, 0.5), Up(1, 0.5), Up(2, 0.5)]);
        var sentUpFailed = Cell(records, [Up(0, 0.5), Up(1, 0.5), Up(2, 0.5), Up(3, 0.5, failed: true)]);

        Assert.True(settledFailed.MissRateUpper > plain.MissRateUpper);
        Assert.Equal(plain.MissRateLower, settledFailed.MissRateLower);
        Assert.True(sentUpFailed.MissRateLower < plain.MissRateLower);
        Assert.Equal(plain.MissRateUpper, sentUpFailed.MissRateUpper);
        Assert.Equal(plain.MissRate, sentUpFailed.MissRate);
    }

    [Fact]
    public void MixedProbabilities_BoundEachCountAtHalfTheLevel()
    {
        var records = new[] { Settled(0, 0.5, mustCatch: true, RecheckOutcome.Disagreed), Settled(1, 0.1) };
        var cell = Cell(records, [Up(0, 0.5), Up(1, 0.1), Up(2, 0.5)]);

        var u = TrustLedger.BinomialCountUpperBound(1, 0.1, 0.025);
        var l = TrustLedger.BinomialCountLowerBound(3, 0.5, 0.025);
        Assert.Equal(u / (u + l), cell.MissRateUpper!.Value, 10);
        Assert.Equal(2.0 / (2.0 + 14), cell.MissRate!.Value, 10);
    }

    [Fact]
    public void ASightedJudgment_IsNotCounted_AndNothingSeenGivesNoBound()
    {
        var cell = Cell([Settled(0, 0.2, mustCatch: true, blind: false)], [Up(0, 0.2, blind: false)]);

        Assert.Equal(0, cell.SentUpRechecks);
        Assert.Null(cell.MissRate);
        Assert.Null(cell.MissRateUpper);
        Assert.Null(cell.MissRateLower);
    }

    [Fact]
    public void ACountAndASampleTogether_OrAKnobWithASample_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => Cell([], [Up(0, 0.2)], caught: 1));
        Assert.Throws<ArgumentException>(() => TrustLedger.Compute([], "ml", "a", Start, End, knob: "uniform", sentUp: [Up(0, 0.2)]));
        Assert.Throws<ArgumentException>(() => Cell([], [Up(0, 0)]));
    }

    // 200 agreed settled rechecks and a few confirmed sent-up decisions, one probability: the share bound is wide because the
    // must-catch decisions seen are few, while the bound on how many were missed is the settled rechecks' own.
    private static LedgerCell NoMissSampled(int caughtSeen)
        => Cell(Enumerable.Range(0, 400).Select(i => Settled(i, 0.5)), Enumerable.Range(0, caughtSeen).Select(i => Up(i, 0.5)));

    private static LifecyclePolicy Policy(double hiddenAllowance = 0) => new()
    {
        Targets = new TrustTargets
        {
            Agreement = 0.95,
            DetectableDrop = 0.03,
            MissLimit = 0.02,
            Deadline = TimeSpan.FromDays(7),
            HiddenMustCatchLimit = hiddenAllowance,
        },
        Margin = 0.01,
        MinimumEffectiveSample = 300,
        MinimumDwell = TimeSpan.FromDays(7),
    };

    [Fact]
    public void TheExposureOfASampledCell_IsTheCountBoundOfTheSettledRechecks_NotTheShareTimesAnEstimate()
    {
        var cell = NoMissSampled(4);

        Assert.Equal(TrustLedger.BinomialCountUpperBound(0, 0.5, 0.05), cell.MissesUpper!.Value, 10);
        Assert.Equal(cell.MissesUpper, TrustController.Exposure(cell));
        Assert.NotEqual(cell.MissRateUpper!.Value * cell.Caught / (1 - cell.MissRateUpper.Value), TrustController.Exposure(cell)!.Value, 3);
        Assert.Null(TrustController.Exposure(Cell([], [Up(0, 0.5)])));                       // nothing settled was looked at
    }

    [Fact]
    public void ASampledCell_IsHeldByItsShareBound_AndReleasedByTheHiddenAllowance()
    {
        var standing = SliceStanding.New("v1", Start.AddDays(-30));
        var signals = new LiveSignals("v1");

        var held = TrustLifecycle.Release(standing, NoMissSampled(4), teacherQualified: true, signals, Policy(), End);
        var allowed = TrustLifecycle.Release(standing, NoMissSampled(4), teacherQualified: true, signals, Policy(hiddenAllowance: 5), End);

        Assert.Equal(TrustState.Shadow, held.To);
        Assert.Contains("miss rate upper bound", held.Reason);
        Assert.Equal(TrustState.Active, allowed.To);
    }

    [Fact]
    public void ASampledCellShowingMissesOverTheLimit_SendsTheSliceBackToShadow()
    {
        var cell = Cell(
            Enumerable.Range(0, 400).Select(i => Settled(i, 0.5, mustCatch: i < 30, i < 30 ? RecheckOutcome.Disagreed : RecheckOutcome.Agreed)),
            Enumerable.Range(0, 5).Select(i => Up(i, 0.5)));

        var step = TrustLifecycle.Tighten(new SliceStanding(TrustState.Active, Start, "v1"), cell, new LiveSignals("v1"), Policy(), End);

        Assert.True(cell.MissRateLower > 0.02);
        Assert.Equal(TrustState.Shadow, step.To);
        Assert.Contains("miss rate shown above", step.Reason);
    }

    [Fact]
    public async Task OneQueueOverBoth_IsAccountedApart_AtOneReach()
    {
        PoolItem Item(string id, bool sentUp) => new()
        {
            DecisionId = id,
            TierId = "ml",
            SliceKey = "a",
            DecidedAt = Start,
            Probability = 0.4,
            SentUp = sentUp,
            InputRef = id,
            ExpiresAt = Start.AddDays(1),
        };
        var store = new InMemoryRecheckPoolStore();
        foreach (var (id, sentUp) in new[] { ("s0", false), ("s1", false), ("u0", true), ("u1", true) })
            Assert.True(await store.AddAsync(Item(id, sentUp)));
        async Task Judge(string id, PoolResponseKind kind, bool mustCatch) => await store.RespondAsync(new PoolResponse
        {
            DecisionId = id,
            TierId = "ml",
            CheckedBy = "people",
            ReviewerKey = "r",
            RespondedAt = Start.AddHours(1),
            Kind = kind,
            Outcome = kind == PoolResponseKind.Answered && !id.StartsWith('u') ? RecheckOutcome.Agreed : null,
            UpperMustCatch = mustCatch,
        });
        await Judge("s0", PoolResponseKind.Answered, false);
        await Judge("u0", PoolResponseKind.Answered, true);

        var account = await RecheckPool.AccountAsync(store, Start, End, Start.AddDays(2));

        Assert.True(account.Closed);
        Assert.Equal("s0", Assert.Single(account.Records).DecisionId);
        var up = Assert.Single(account.SentUp);
        Assert.Equal("u0", up.DecisionId);
        Assert.True(up.UpperMustCatch);
        Assert.Equal(0.2, up.Probability, 10);                      // 0.4 × reach 2 of 4, the settled ones' reach too
        Assert.Equal(0.2, account.Records[0].Probability, 10);
    }
}
