using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HoneAI.Learning;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class WindowCloseTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(14);
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    private static readonly ControllerPolicy Policy = new(
        new LifecyclePolicy
        {
            Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = 0.02, Deadline = TimeSpan.FromDays(7) },
            Margin = 0.01,
            MinimumEffectiveSample = 300,
            MinimumDwell = TimeSpan.FromDays(7),
            StableWindows = 2,
        },
        new IssuePolicy { SoftLife = TimeSpan.FromDays(1), HardLife = TimeSpan.FromDays(3), ActiveRate = 0.3 },
        new Dictionary<string, double> { ["tenant-a"] = 20_000 });

    private static readonly TeacherPolicy Floor = new() { MinimumRecall = 0.8, Margin = 0.05 };

    private static WindowCloseInput Input(IReadOnlyList<WindowSlice> slices, IReadOnlyList<LearnerWindow>? learners = null, TeacherPolicy? teacher = null) => new()
    {
        Version = "s-1",
        Now = Now,
        Window = Window,
        Slices = slices,
        Policy = Policy,
        Teacher = teacher ?? Floor,
        Learners = learners ?? [],
    };

    private static WindowSlice Slice(string tier = "ml", string slice = "s", string? teacher = null, string version = "v1", bool drifted = false) =>
        new(tier, slice, "tenant-a", new SliceTraffic(10_000, 3_000), version) { TeacherTierId = teacher, Caught = 400, Drifted = drifted };

    private static WindowCloseState At(TrustState state, string version = "v1", int passing = 0, string tier = "ml", string slice = "s", bool? teacherQualified = null, string teacher = "llm")
    {
        var qualified = new Dictionary<string, bool>();
        if (teacherQualified is { } q)
            qualified[teacher] = q;
        return new WindowCloseState
        {
            Standings = new Dictionary<string, SliceStanding> { [WindowCloseState.Key(tier, slice)] = new(state, Start, version, passing) },
            QualifiedTeachers = qualified,
        };
    }

    // 400 agreeing rechecks inside the closed window: enough effective sample, agreement near 1, no misses.
    private static List<RecheckRecord> Agreeing(string tier = "ml", string slice = "s", string? checkedBy = null, string? checkedByVersion = null, DateTimeOffset? at = null, int count = 400)
        => [.. Enumerable.Range(0, count).Select(i => new RecheckRecord
        {
            DecisionId = $"{tier}-{slice}-{(at ?? Now.AddDays(-3)):yyyyMMdd}-{i}",
            TierId = tier,
            SliceKey = slice,
            DecidedAt = (at ?? Now.AddDays(-3)).AddMinutes(i),
            Probability = 1,
            Outcome = RecheckOutcome.Agreed,
            CheckedBy = checkedBy,
            CheckedByVersion = checkedByVersion,
        })];

    private static List<PlantRecord> Plants(string teacher, int caught, int missed, string? version = null)
        => [.. Enumerable.Range(0, caught + missed).Select(i => new PlantRecord
        {
            PlantId = $"{teacher}-{i}",
            TierId = teacher,
            SliceKey = "s",
            JudgedAt = Now.AddDays(-2).AddMinutes(i),
            TruthMustCatch = true,
            Outcome = i < caught ? PlantOutcome.Correct : PlantOutcome.Wrong,
            AnsweredMustCatch = i < caught,
            TierVersion = version,
        })];

    private static WindowRecords Records(List<RecheckRecord> now, List<RecheckRecord>? previous = null, List<PlantRecord>? plants = null)
        => new(now, previous ?? [], plants ?? []);

    private static readonly string Key = WindowCloseState.Key("ml", "s");

    [Fact]
    public void AReleaseWaitsForAPerson_AndTheKeptStandingIsTheCommittedOne()
    {
        var result = WindowClose.Run(Input([Slice()]), Records(Agreeing()), At(TrustState.Shadow));

        var outcome = Assert.Single(result.Step.Slices);
        Assert.True(outcome.Step.AwaitsApproval);
        Assert.Equal(TrustState.Active, outcome.Step.Standing.State);
        Assert.Equal(["s"], result.Step.AwaitingApproval.Select(s => s.SliceKey));
        // Nothing is released on the driver's say-so: the snapshot and the kept state still say shadow.
        Assert.Equal(TrustState.Shadow, result.Step.Snapshot.Entries.Single().State);
        Assert.Equal(TrustState.Shadow, result.State.Standings[Key].State);
    }

    [Fact]
    public void ANewSliceStartsInShadow_AndTheStateRemembersIt()
    {
        var result = WindowClose.Run(Input([Slice()]), Records(Agreeing()), WindowCloseState.Empty);

        Assert.Equal(TrustState.Shadow, result.State.Standings[Key].State);
        Assert.Equal(Now, result.State.Standings[Key].Since);
        Assert.Empty(result.Step.AwaitingApproval);
    }

    [Fact]
    public void TheSameEvidenceStateAndInputs_GiveTheSameClose()
    {
        var learner = new LearnerWindow("ml", new RetrainPolicy { MinimumNewLabels = 100, MinimumInterval = TimeSpan.FromDays(1) }, new RetrainSignals { NewLabels = 120 });
        string Run() => JsonSerializer.Serialize(WindowClose.Run(Input([Slice(teacher: "llm")], [learner]),
            Records(Agreeing(), plants: Plants("llm", 60, 0)), At(TrustState.Shadow)));

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ATeacherShownUnqualified_TightensTheStableTierItTaught()
    {
        var result = WindowClose.Run(Input([Slice(teacher: "llm")]), Records(Agreeing(), plants: Plants("llm", caught: 0, missed: 20)), At(TrustState.Stable, teacherQualified: true));

        var teacher = Assert.Single(result.Teachers);
        Assert.True(teacher.Disqualified);
        Assert.False(teacher.Qualified);
        var outcome = Assert.Single(result.Step.Slices);
        Assert.Equal(TrustState.Active, outcome.Step.Standing.State);
        Assert.Contains("unqualified", outcome.Step.Reason, StringComparison.Ordinal);
        Assert.False(result.State.QualifiedTeachers["llm"]);
    }

    [Fact]
    public void ATeacherOnTheLine_KeepsItsQualification_UntilItsBoundIsUnderTheFloorLessTheMargin()
    {
        var plants = Plants("llm", caught: 29, missed: 1);
        var cell = TeacherLedger.Compute(plants, "llm", Now - Window, Now);
        var floor = new TeacherPolicy { MinimumRecall = Math.Round(cell.RecallLower!.Value + 0.02, 3), Margin = 0.05 };
        Assert.True(cell.RecallLower < floor.MinimumRecall && cell.RecallLower >= floor.MinimumRecall - floor.Margin);

        var wasQualified = WindowClose.Run(Input([Slice(teacher: "llm")], teacher: floor), Records(Agreeing(), plants: plants), At(TrustState.Active, teacherQualified: true));
        var wasNot = WindowClose.Run(Input([Slice(teacher: "llm")], teacher: floor), Records(Agreeing(), plants: plants), At(TrustState.Active, teacherQualified: false));

        Assert.True(Assert.Single(wasQualified.Teachers).Qualified);
        Assert.False(Assert.Single(wasNot.Teachers).Qualified);
        Assert.False(Assert.Single(wasQualified.Teachers).Disqualified);
    }

    [Fact]
    public void ANewTeacherVersion_IsReadFromTheRecords_AndTightensAStableTier()
    {
        var before = Agreeing(checkedBy: "llm", checkedByVersion: "t1", at: Now.AddDays(-10));
        var now = Agreeing(checkedBy: "llm", checkedByVersion: "t2");

        var result = WindowClose.Run(Input([Slice(teacher: "llm")]), Records(now, before, Plants("llm", 60, 0)), At(TrustState.Stable, teacherQualified: true));

        var outcome = Assert.Single(result.Step.Slices);
        Assert.Equal(TrustState.Active, outcome.Step.Standing.State);
        Assert.Contains("new version", outcome.Step.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADemotionWithinAVersion_CountsTowardRetraining_ButARestartOnANewVersionDoesNot()
    {
        var policy = new RetrainPolicy { MinimumNewLabels = 100, MinimumInterval = TimeSpan.FromDays(1), OnStaleness = false };
        var signals = new RetrainSignals { NewLabels = 5 };

        // Stable on v1, drifted, still v1: tightened to active — a demotion, and the model has 5 new labels.
        var demoted = WindowClose.Run(Input([Slice(drifted: true)], [new LearnerWindow("ml", policy, signals)]), Records(Agreeing()), At(TrustState.Stable));
        var decision = Assert.Single(demoted.Retrains).Decision;
        Assert.True(decision.Start);
        Assert.Contains("demoted", decision.Reason, StringComparison.Ordinal);

        // Stable on v1, the tier is now v2: its standing starts over, which is not the ledger finding it worse.
        var restarted = WindowClose.Run(Input([Slice(version: "v2")], [new LearnerWindow("ml", policy, signals)]), Records(Agreeing()), At(TrustState.Stable));
        Assert.Equal(TrustState.Shadow, restarted.Step.Slices.Single().Step.Standing.State);
        Assert.False(Assert.Single(restarted.Retrains).Decision.Start);
    }

    [Fact]
    public void ARunInFlight_IsNeverStartedTwice_WhateverTheWindowSays()
    {
        var policy = new RetrainPolicy { MinimumNewLabels = 100, MinimumInterval = TimeSpan.FromDays(1) };
        var result = WindowClose.Run(Input([Slice(drifted: true)], [new LearnerWindow("ml", policy, new RetrainSignals { NewLabels = 500, RunInFlight = true })]),
            Records(Agreeing()), At(TrustState.Stable));

        Assert.False(Assert.Single(result.Retrains).Decision.Start);
    }

    [Fact]
    public async Task TheCloser_ReadsTheStores_KeepsTheState_AndTheNextCloseStartsFromIt()
    {
        var rechecks = new InMemoryRecheckStore();
        var plants = new InMemoryPlantStore();
        var state = new InMemoryWindowCloseState();
        foreach (var r in Agreeing())
            await rechecks.AppendAsync(r);
        foreach (var p in Plants("llm", 60, 0))
            await plants.AppendAsync(p);
        await state.SaveAsync(At(TrustState.Shadow));

        var closer = new WindowCloser(rechecks, plants, state);
        var result = await closer.CloseAsync(Input([Slice(teacher: "llm")]));

        Assert.True(Assert.Single(result.Step.Slices).Step.AwaitsApproval);
        Assert.True(Assert.Single(result.Teachers).Qualified);
        var kept = await state.LoadAsync();
        Assert.Equal(TrustState.Shadow, kept.Standings[Key].State);
        Assert.True(kept.QualifiedTeachers["llm"]);

        // The person approves; the caller commits the release; the next window judges from there.
        var approved = result.Step.Slices.Single().Step.Standing;
        await state.SaveAsync(kept with { Standings = new Dictionary<string, SliceStanding> { [Key] = approved } });
        var next = await closer.CloseAsync(Input([Slice(teacher: "llm")]) with { Version = "s-2" });
        Assert.Equal(TrustState.Active, (await state.LoadAsync()).Standings[Key].State);
        Assert.Equal("s-2", next.Step.Snapshot.Version);
    }

    [Fact]
    public void AnApproval_KeepsTheStandingTheReleaseWouldBecome_AndRefusesWhatIsNotWaitingOrIsOutOfDate()
    {
        var result = WindowClose.Run(Input([Slice()]), Records(Agreeing()), At(TrustState.Shadow));
        var waiting = Assert.Single(result.Step.AwaitingApproval);

        var approved = result.State.Approve(waiting);
        Assert.Equal(TrustState.Active, approved.Standings[Key].State);

        // The same approval twice: the slice is no longer where the release was judged from.
        Assert.Throws<InvalidOperationException>(() => approved.Approve(waiting));
        // A tightening or a quiet slice has nothing waiting.
        var quiet = WindowClose.Run(Input([Slice()]), Records(Agreeing()), At(TrustState.Stable));
        Assert.Throws<ArgumentException>(() => quiet.State.Approve(Assert.Single(quiet.Step.Slices)));
        // An approval of one close cannot be committed on a state another close moved.
        var moved = result.State with { Standings = new Dictionary<string, SliceStanding> { [Key] = new(TrustState.Shadow, Now, "v2") } };
        Assert.Throws<InvalidOperationException>(() => moved.Approve(waiting));
    }

    [Fact]
    public void ATeacherAtTwoVersionsInOneClose_IsRefused()
    {
        var slices = new[] { Slice(slice: "a", teacher: "llm") with { TeacherVersion = "t1" }, Slice(slice: "b", teacher: "llm") with { TeacherVersion = "t2" } };
        Assert.Throws<ArgumentException>(() => WindowClose.Run(Input(slices), Records([]), WindowCloseState.Empty));
    }
}
