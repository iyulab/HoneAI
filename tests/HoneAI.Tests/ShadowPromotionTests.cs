using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class ShadowPromotionTests
{
    private static readonly DateTimeOffset Trained = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly PromotionCandidate Candidate = new("run-1", "weld", "exp-9") { TrainedAt = Trained };

    private static ShadowPromotionGate Gate(IReadOnlyCollection<SliceStanding> standings, DateTimeOffset now)
        => new((_, _) => Task.FromResult(standings), TimeSpan.FromDays(14), clock: () => now);

    [Fact]
    public async Task AShadow_WaitsUntilEverySliceReleasedIt_ThenPromotes()
    {
        Assert.Equal(PromotionDecision.Wait, (await Gate([], Trained.AddDays(1)).JudgeAsync(Candidate)).Decision);

        var partly = Gate([new(TrustState.Active, Trained.AddDays(3), "exp-9"), SliceStanding.New("exp-9", Trained)], Trained.AddDays(4));
        var waiting = await partly.JudgeAsync(Candidate);
        Assert.Equal(PromotionDecision.Wait, waiting.Decision);
        Assert.Contains("1 shadow, 1 active", waiting.Reason);

        var released = Gate([new(TrustState.Active, Trained.AddDays(3), "exp-9"), new(TrustState.Stable, Trained.AddDays(6), "exp-9")], Trained.AddDays(7));
        var verdict = await released.JudgeAsync(Candidate);
        Assert.Equal(PromotionDecision.Promote, verdict.Decision);
        Assert.False(verdict.ProvenNoWorse);                                        // it earned its own standing
    }

    [Fact]
    public async Task AShadow_ThatDidNotEarnItsStandingInTime_IsRefused_AndOtherVersionsDoNotCount()
    {
        var late = await Gate([SliceStanding.New("exp-9", Trained)], Trained.AddDays(15)).JudgeAsync(Candidate);
        Assert.Equal(PromotionDecision.Refuse, late.Decision);
        Assert.Contains("within 14.00:00:00", late.Reason);

        // Production's version is active everywhere; the candidate is not watched at all.
        var other = await Gate([new(TrustState.Active, Trained, "exp-2")], Trained.AddDays(1)).JudgeAsync(Candidate);
        Assert.Equal(PromotionDecision.Wait, other.Decision);
        Assert.Contains("not watched in shadow yet", other.Reason);
    }

    [Fact]
    public void Rollback_IsDemandedWhenTheSameVersionFallsToShadow_NotWhenANewVersionStartsThere()
    {
        var policy = new LifecyclePolicy
        {
            Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
            Margin = 0.01,
            MinimumEffectiveSample = 300,
            MinimumDwell = TimeSpan.FromDays(7),
        };
        var active = new SliceStanding(TrustState.Active, Trained, "exp-9");

        var demoted = new StandingChange(TrustState.Active, TrustState.Shadow, "must-catch miss rate shown above 2%", false,
            new SliceStanding(TrustState.Shadow, Trained.AddDays(2), "exp-9"));
        Assert.True(RollbackRule.Demands(active, demoted));

        var stableToActive = new StandingChange(TrustState.Stable, TrustState.Active, "a must-catch miss was seen", false, active);
        Assert.False(RollbackRule.Demands(active with { State = TrustState.Stable }, stableToActive));

        // The tier's version changed (the candidate went in): its new standing starts in shadow — not a demotion of it.
        var changed = TrustLifecycle.Tighten(active, Running(), new LiveSignals("exp-10"), policy, Trained.AddDays(2));
        Assert.Equal(TrustState.Shadow, changed.To);
        Assert.False(RollbackRule.Demands(active, changed));
    }

    private static LedgerCell Running() => new()
    {
        TierId = "ml",
        SliceKey = "s",
        WindowStart = Trained,
        WindowEnd = Trained.AddDays(2),
        Rechecks = 500,
        UpperFailures = 0,
        EffectiveSampleSize = 500,
        Agreement = 0.98,
        AgreementLower = 0.97,
        AgreementUpper = 0.99,
        MissesEstimated = 0,
        Caught = 400,
        MissRate = 0,
        MissRateUpper = 0.01,
        MissRateLower = 0,
        Confidence = 0.95,
    };
}
