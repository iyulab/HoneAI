using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// A teacher on the line should not flip between qualified and not with every window, and a tier that was released on a teacher who
/// is then shown unqualified should not keep the standing it earned from rechecks that teacher can no longer be trusted to make.
/// </summary>
public class TeacherQualificationMarginTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly LifecyclePolicy Policy = new()
    {
        Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
        Margin = 0.01, MinimumEffectiveSample = 300, MinimumDwell = TimeSpan.FromDays(7), StableWindows = 2,
    };

    private static TeacherCell Teacher(int caught, int missed, int failed = 0)
    {
        var records = new List<PlantRecord>();
        for (var i = 0; i < caught; i++) records.Add(Plant($"c{i}", PlantOutcome.Correct, true));
        for (var i = 0; i < missed; i++) records.Add(Plant($"m{i}", PlantOutcome.Wrong, false));
        for (var i = 0; i < failed; i++) records.Add(Plant($"f{i}", PlantOutcome.Failed, false));
        return TeacherLedger.Compute(records, "teacher", Now.AddHours(-1), Now);
    }

    private static PlantRecord Plant(string id, PlantOutcome outcome, bool answeredMustCatch) => new()
    {
        PlantId = id, TierId = "teacher", SliceKey = "s", JudgedAt = Now.AddMinutes(-10),
        TruthMustCatch = true, Outcome = outcome, AnsweredMustCatch = answeredMustCatch,
    };

    [Fact]
    public void TheUpperBound_IsTheMostThePlantsAllow_FailuresCountedAsCaught()
    {
        var cell = Teacher(caught: 36, missed: 2, failed: 2);

        Assert.Equal(1 - TeacherLedger.ClopperPearsonLower(2, 40, 0.05), cell.RecallUpper!.Value, 12);   // two answered misses; failures given the benefit
        Assert.True(cell.RecallUpper > cell.Recall);
        Assert.True(cell.RecallUpper > cell.RecallLower);
        Assert.Null(TeacherLedger.Compute([], "teacher", Now.AddHours(-1), Now).RecallUpper);
    }

    [Fact]
    public void AQualifiedTeacher_KeepsItsStandingInsideTheBand_ButNotBelowIt()
    {
        // Floor 0.9, margin 0.05: a qualified teacher stays while its lower bound is at 0.85 or over.
        var noisy = Teacher(caught: 20, missed: 1);        // lower bound ≈ 0.78 — under the floor less the margin
        var steady = Teacher(caught: 38, missed: 2);       // lower bound ≈ 0.86 — under the floor, inside the band

        Assert.False(TeacherLedger.Qualifies(steady, 0.9));
        Assert.True(TeacherLedger.Qualifies(steady, 0.9, margin: 0.05, wasQualified: true));            // inside the band: stays
        Assert.False(TeacherLedger.Qualifies(steady, 0.9, margin: 0.05, wasQualified: false));          // in the band, not yet: no
        Assert.False(TeacherLedger.Qualifies(noisy, 0.9, margin: 0.05, wasQualified: true));            // under the band: loses it
    }

    [Fact]
    public void AWindowWithNoMustCatchPlant_ChangesNothing()
    {
        var empty = TeacherLedger.Compute([], "teacher", Now.AddHours(-1), Now);

        Assert.True(TeacherLedger.Qualifies(empty, 0.9, 0.05, wasQualified: true));
        Assert.False(TeacherLedger.Qualifies(empty, 0.9, 0.05, wasQualified: false));
        Assert.False(TeacherLedger.Disqualifies(empty, 0.9, 0.05));
    }

    [Fact]
    public void ATeacherIsShownUnqualified_OnlyWhenEvenItsMostIsUnderTheFloorLessTheMargin()
    {
        var weak = Teacher(caught: 10, missed: 30);        // most it can be: well under 0.85
        var thin = Teacher(caught: 1, missed: 1);          // two plants: too few to say
        var good = Teacher(caught: 39, missed: 1);

        Assert.True(TeacherLedger.Disqualifies(weak, 0.9, 0.05));
        Assert.False(TeacherLedger.Disqualifies(thin, 0.9, 0.05));
        Assert.False(TeacherLedger.Disqualifies(good, 0.9, 0.05));
        Assert.False(TeacherLedger.Qualifies(thin, 0.9));       // unproven is not disqualified, nor qualified
    }

    [Fact]
    public void AStableTier_FallsBackToActive_WhenItsTeacherIsShownUnqualified()
    {
        var stable = new SliceStanding(TrustState.Stable, Start, "v1");
        var cell = new LedgerCell { TierId = "ml", SliceKey = "s", WindowStart = Start, WindowEnd = Now, Rechecks = 500, UpperFailures = 0,
            EffectiveSampleSize = 500, Agreement = 0.98, AgreementLower = 0.97, AgreementUpper = 0.99, MissesEstimated = 0, Caught = 400,
            MissRate = 0, MissRateUpper = 0.01, MissRateLower = 0, Confidence = 0.95 };

        var held = TrustLifecycle.Tighten(stable, cell, new LiveSignals("v1"), Policy, Now);
        var lost = TrustLifecycle.Tighten(stable, cell, new LiveSignals("v1", TeacherDisqualified: true), Policy, Now);

        Assert.False(held.Moved);
        Assert.Equal(TrustState.Active, lost.To);
        Assert.False(lost.AwaitsApproval);      // tightening never waits
        Assert.Contains("unqualified", lost.Reason);
    }

    [Fact]
    public void AnActiveTiersRunTowardStable_StartsOver_AndAShadowTierIsUntouched()
    {
        var active = new SliceStanding(TrustState.Active, Start, "v1", PassingWindows: 1);
        var cell = new LedgerCell { TierId = "ml", SliceKey = "s", WindowStart = Start, WindowEnd = Now, Rechecks = 500, UpperFailures = 0,
            EffectiveSampleSize = 500, Agreement = 0.98, AgreementLower = 0.97, AgreementUpper = 0.99, MissesEstimated = 0, Caught = 400,
            MissRate = 0, MissRateUpper = 0.01, MissRateLower = 0, Confidence = 0.95 };
        var signals = new LiveSignals("v1", TeacherDisqualified: true);

        var run = TrustLifecycle.Tighten(active, cell, signals, Policy, Now);
        var shadow = TrustLifecycle.Tighten(SliceStanding.New("v1", Start), cell, signals, Policy, Now);

        Assert.Equal(TrustState.Active, run.To);
        Assert.Equal(0, run.Standing.PassingWindows);
        Assert.False(shadow.Moved);
    }
}
