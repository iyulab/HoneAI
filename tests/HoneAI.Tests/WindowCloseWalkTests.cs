using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI.Learning;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>A consumer's weeks: the driver closes a window every seven days, a person approves what waits, and the world moves.</summary>
public class WindowCloseWalkTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

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

    private static readonly RetrainPolicy Retrain = new() { MinimumNewLabels = 100, MinimumInterval = TimeSpan.FromDays(1) };

    private static List<RecheckRecord> Rechecks(DateTimeOffset windowEnd, int count = 400)
        => [.. Enumerable.Range(0, count).Select(i => new RecheckRecord
        {
            DecisionId = $"d-{windowEnd:yyyyMMdd}-{i}",
            TierId = "ml",
            SliceKey = "s",
            DecidedAt = windowEnd.AddDays(-3).AddMinutes(i),
            Probability = 1,
            Outcome = RecheckOutcome.Agreed,
            CheckedBy = "llm",
            CheckedByVersion = "t1",
        })];

    private static List<PlantRecord> Plants(DateTimeOffset windowEnd, int caught, int missed)
        => [.. Enumerable.Range(0, caught + missed).Select(i => new PlantRecord
        {
            PlantId = $"p-{windowEnd:yyyyMMdd}-{i}",
            TierId = "llm",
            SliceKey = "s",
            JudgedAt = windowEnd.AddDays(-2).AddMinutes(i),
            TruthMustCatch = true,
            Outcome = i < caught ? PlantOutcome.Correct : PlantOutcome.Wrong,
            AnsweredMustCatch = i < caught,
        })];

    [Fact]
    public async Task AModelTierFromShadowToStable_ThroughThePersonsApprovals_AndBackDownWhenTheWorldMoves()
    {
        var rechecks = new InMemoryRecheckStore();
        var plants = new InMemoryPlantStore();
        var state = new InMemoryWindowCloseState();
        var closer = new WindowCloser(rechecks, plants, state);
        var key = WindowCloseState.Key("ml", "s");

        async Task Fill(DateTimeOffset end, int plantsCaught = 60, int plantsMissed = 0)
        {
            foreach (var r in Rechecks(end))
                await rechecks.AppendAsync(r);
            foreach (var p in Plants(end, plantsCaught, plantsMissed))
                await plants.AppendAsync(p);
        }

        async Task<WindowCloseResult> Close(int week, bool drifted = false, double newLabels = 0)
        {
            var now = Start + week * Week;
            var slice = new WindowSlice("ml", "s", "tenant-a", new SliceTraffic(10_000, 3_000), "v1") { TeacherTierId = "llm", Caught = 400, Drifted = drifted };
            return await closer.CloseAsync(new WindowCloseInput
            {
                Version = $"s-{week}",
                Now = now,
                Window = Week,
                Slices = [slice],
                Policy = Policy,
                Teacher = new TeacherPolicy { MinimumRecall = 0.8, Margin = 0.05 },
                Learners = [new LearnerWindow("ml", Retrain, new RetrainSignals { NewLabels = newLabels })],
            });
        }

        // The standing is kept from week 1: a new slice starts in shadow, its dwell begins there.
        await Fill(Start + Week);
        var w1 = await Close(1);
        Assert.Equal(TrustState.Shadow, w1.State.Standings[key].State);
        Assert.Empty(w1.Step.AwaitingApproval);

        // Week 2: the dwell has passed, every release condition holds — a release waits for a person.
        await Fill(Start + 2 * Week);
        var w2 = await Close(2);
        Assert.Single(w2.Step.AwaitingApproval);
        Assert.Equal(TrustState.Shadow, w2.State.Standings[key].State);
        await closer.ApproveAsync(Assert.Single(w2.Step.AwaitingApproval));

        // Weeks 3 and 4: active; the second releasing window in a row makes it stable, again for a person to approve.
        await Fill(Start + 3 * Week);
        var w3 = await Close(3);
        Assert.Empty(w3.Step.AwaitingApproval);
        Assert.Equal(1, w3.State.Standings[key].PassingWindows);
        await Fill(Start + 4 * Week);
        var w4 = await Close(4);
        Assert.Single(w4.Step.AwaitingApproval);
        await closer.ApproveAsync(Assert.Single(w4.Step.AwaitingApproval));
        Assert.Equal(TrustState.Stable, (await state.LoadAsync()).Standings[key].State);

        // Week 5: stable and quiet.
        await Fill(Start + 5 * Week);
        var w5 = await Close(5);
        Assert.Equal(TrustState.Stable, w5.State.Standings[key].State);
        Assert.False(Assert.Single(w5.Retrains).Decision.Start);

        // Week 6: the inputs drift — stable falls back to active at once, and the model, with 5 new labels, is retrained.
        await Fill(Start + 6 * Week);
        var w6 = await Close(6, drifted: true, newLabels: 5);
        Assert.Equal(TrustState.Active, w6.State.Standings[key].State);
        Assert.True(Assert.Single(w6.Retrains).Decision.Start);

        // Week 7: the teacher misses what it should catch — the active tier's run toward stable starts over and nothing releases.
        await Fill(Start + 7 * Week, plantsCaught: 0, plantsMissed: 20);
        var w7 = await Close(7);
        Assert.True(Assert.Single(w7.Teachers).Disqualified);
        Assert.Empty(w7.Step.AwaitingApproval);
        Assert.Equal(0, w7.State.Standings[key].PassingWindows);
    }
}
