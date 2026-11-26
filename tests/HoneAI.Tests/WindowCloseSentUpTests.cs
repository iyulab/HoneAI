using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

/// <summary>A window closed on a slice whose sent-up decisions the tier above judges only by sample.</summary>
public class WindowCloseSentUpTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(14);
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);
    private static readonly DateTimeOffset InWindow = Now.AddDays(-3);
    private static readonly DateTimeOffset Before = Now.AddDays(-10);

    private static ControllerPolicy Policy(double missLimit = 0.02) => new(
        new LifecyclePolicy
        {
            Targets = new TrustTargets { Agreement = 0.95, DetectableDrop = 0.03, MissLimit = missLimit, Deadline = TimeSpan.FromDays(7) },
            Margin = 0.01,
            MinimumEffectiveSample = 300,
            MinimumDwell = TimeSpan.FromDays(7),
            StableWindows = 2,
        },
        new IssuePolicy { SoftLife = TimeSpan.FromDays(1), HardLife = TimeSpan.FromDays(3), ActiveRate = 0.3 },
        new Dictionary<string, double> { ["tenant-a"] = 20_000 });

    private static WindowCloseInput Input(WindowSlice slice, double missLimit = 0.02) => new()
    {
        Version = "s-1",
        Now = Now,
        Window = Window,
        Slices = [slice],
        Policy = Policy(missLimit),
        Teacher = new TeacherPolicy { MinimumRecall = 0.8, Margin = 0.05 },
    };

    private static WindowSlice Sampled(int caught = 0) =>
        new("ml", "s", "tenant-a", new SliceTraffic(10_000, 3_000), "v1") { SentUpSampled = true, Caught = caught };

    private static WindowSlice Counted(int caught) =>
        new("ml", "s", "tenant-a", new SliceTraffic(10_000, 3_000), "v1") { Caught = caught };

    private static WindowCloseState At(TrustState state) => new()
    {
        Standings = new Dictionary<string, SliceStanding> { [WindowCloseState.Key("ml", "s")] = new(state, Start, "v1") },
    };

    private static RecheckRecord Settled(int i, bool miss = false, DateTimeOffset? at = null, double pi = 0.5, string? checkedByVersion = null) => new()
    {
        DecisionId = $"s-{(at ?? InWindow):yyyyMMdd}-{i}",
        TierId = "ml",
        SliceKey = "s",
        DecidedAt = (at ?? InWindow).AddMinutes(i),
        Probability = pi,
        Outcome = miss ? RecheckOutcome.Disagreed : RecheckOutcome.Agreed,
        UpperMustCatch = miss,
        CheckedBy = checkedByVersion is null ? null : "llm",
        CheckedByVersion = checkedByVersion,
    };

    private static SentUpRecheck Up(int i, DateTimeOffset? at = null, double pi = 0.5, string tier = "ml", string slice = "s", string? checkedByVersion = null) => new()
    {
        DecisionId = $"u-{(at ?? InWindow):yyyyMMdd}-{i}",
        TierId = tier,
        SliceKey = slice,
        DecidedAt = (at ?? InWindow).AddMinutes(i),
        Probability = pi,
        UpperMustCatch = true,
        CheckedBy = checkedByVersion is null ? null : "llm",
        CheckedByVersion = checkedByVersion,
    };

    private static List<RecheckRecord> OneMissAmong(int agreed, double pi = 0.5)
        => [.. Enumerable.Range(0, agreed).Select(i => Settled(i, pi: pi)), Settled(agreed, miss: true, pi: pi)];

    private static WindowRecords Records(List<RecheckRecord> now, List<SentUpRecheck>? sentUp = null, List<RecheckRecord>? previous = null, List<SentUpRecheck>? previousSentUp = null)
        => new(now, previous ?? [], []) { SentUp = sentUp ?? [], PreviousSentUp = previousSentUp ?? [] };

    [Fact]
    public async Task OneQueueOverBoth_ThroughTheStores_ClosesOnTheSample_NotOnACount()
    {
        var pool = new InMemoryRecheckPoolStore();
        var ids = Enumerable.Range(0, 400).Select(i => (Id: $"s{i}", SentUp: false))
            .Concat(Enumerable.Range(0, 3).Select(i => (Id: $"u{i}", SentUp: true)))
            .ToList();
        foreach (var (id, sentUp) in ids)
        {
            await pool.AddAsync(new PoolItem
            {
                DecisionId = id,
                TierId = "ml",
                SliceKey = "s",
                SentUp = sentUp,
                DecidedAt = InWindow,
                Probability = 0.2,
                InputRef = id,
                ExpiresAt = Now,
            });
            var miss = id == "s0";
            await pool.RespondAsync(new PoolResponse
            {
                DecisionId = id,
                TierId = "ml",
                CheckedBy = "people",
                ReviewerKey = "r",
                RespondedAt = InWindow.AddHours(1),
                Kind = PoolResponseKind.Answered,
                Outcome = sentUp ? null : miss ? RecheckOutcome.Disagreed : RecheckOutcome.Agreed,
                UpperMustCatch = sentUp || miss,
            });
        }
        var account = await RecheckPool.AccountAsync(pool, Now - Window, Now, Now.AddDays(1));
        Assert.True(account.Closed);

        var path = Path.Combine(Path.GetTempPath(), $"honeai-sentup-{Guid.NewGuid():N}.jsonl");
        try
        {
            var rechecks = new InMemoryRecheckStore();
            var sentUpStore = new JsonlSentUpRecheckStore(path);
            foreach (var r in account.Records)
                await rechecks.AppendAsync(r);
            foreach (var r in account.SentUp)
                Assert.True(await sentUpStore.AppendAsync(r));
            var state = new InMemoryWindowCloseState();
            await state.SaveAsync(At(TrustState.Shadow));

            var closer = new WindowCloser(rechecks, new InMemoryPlantStore(), state, new JsonlSentUpRecheckStore(path));
            var outcome = Assert.Single((await closer.CloseAsync(Input(Sampled()))).Step.Slices);

            var expected = TrustLedger.Compute(account.Records, "ml", "s", Now - Window, Now, sentUp: account.SentUp);
            Assert.Equal(0.25, expected.MissRate!.Value, 10);
            Assert.Equal(3, expected.SentUpRechecks);
            Assert.Contains($"miss rate upper bound {expected.MissRateUpper:P1}", outcome.Step.Reason);
            var counted = TrustLedger.Compute(account.Records, "ml", "s", Now - Window, Now, caught: 3);
            Assert.NotEqual($"{counted.MissRateUpper:P1}", $"{expected.MissRateUpper:P1}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASampledSliceThatReachedNoSentUpDecision_IsNotReadAsACountOfZero()
    {
        // One miss and no confirmed sent-up decision seen. As a count of zero the miss rate is shown to be 100 %; as a sample
        // of one must-catch decision the lower bound is the 5 % that one observation allows.
        var records = Records(OneMissAmong(400));

        var asCount = Assert.Single(WindowClose.Run(Input(Counted(0), missLimit: 0.1), records, At(TrustState.Active)).Step.Slices);
        var asSample = Assert.Single(WindowClose.Run(Input(Sampled(), missLimit: 0.1), records, At(TrustState.Active)).Step.Slices);

        Assert.Equal(TrustState.Shadow, asCount.Step.To);
        Assert.Contains("miss rate shown above", asCount.Step.Reason);
        Assert.Equal(TrustState.Active, asSample.Step.To);
    }

    [Fact]
    public void ACountOnASampledSlice_IsRefused()
    {
        var refused = Assert.Throws<ArgumentException>(() => WindowClose.Run(Input(Sampled(caught: 3)), Records(OneMissAmong(10)), At(TrustState.Shadow)));

        Assert.Contains("'ml'", refused.Message);
        Assert.Contains(nameof(WindowSlice.SentUpSampled), refused.Message);
    }

    [Fact]
    public void ASampleForASliceThatDoesNotDeclareOne_IsRefused_NotIgnored()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => WindowClose.Run(Input(Counted(3)), Records(OneMissAmong(10), [Up(0)]), At(TrustState.Shadow)));
        Assert.Contains(nameof(WindowSlice.SentUpSampled), refused.Message);

        // Records of another tier, another slice, or outside the closed window are not this slice's.
        var elsewhere = new List<SentUpRecheck> { Up(0, tier: "other"), Up(1, slice: "other"), Up(2, at: Before), Up(3, at: Now) };
        WindowClose.Run(Input(Counted(3)), Records(OneMissAmong(10), elsewhere), At(TrustState.Shadow));
    }

    [Fact]
    public async Task ACountedSlice_ClosesAsBefore_WithASentUpStoreThatHoldsNothingForIt()
    {
        var rechecks = new InMemoryRecheckStore();
        foreach (var r in OneMissAmong(400))
            await rechecks.AppendAsync(r);
        var without = new InMemoryWindowCloseState();
        var with = new InMemoryWindowCloseState();
        await without.SaveAsync(At(TrustState.Shadow));
        await with.SaveAsync(At(TrustState.Shadow));

        var before = await new WindowCloser(rechecks, new InMemoryPlantStore(), without).CloseAsync(Input(Counted(400)));
        var after = await new WindowCloser(rechecks, new InMemoryPlantStore(), with, new InMemorySentUpRecheckStore()).CloseAsync(Input(Counted(400)));

        Assert.Equal(Assert.Single(before.Step.Slices).Step.Reason, Assert.Single(after.Step.Slices).Step.Reason);
        Assert.Equal(Assert.Single(before.Step.Slices).Step.To, Assert.Single(after.Step.Slices).Step.To);
    }

    [Fact]
    public async Task ASampledSlice_NeedsAStoreToReadTheSampleFrom()
    {
        var closer = new WindowCloser(new InMemoryRecheckStore(), new InMemoryPlantStore(), new InMemoryWindowCloseState());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await closer.CloseAsync(Input(Sampled())));

        Assert.Contains(nameof(ISentUpRecheckStore), refused.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATeacherVersionSeenOnlyOnTheSentUpSide_TightensLikeAnyOther(bool throughTheCloser)
    {
        var previous = Enumerable.Range(0, 20).Select(i => Settled(i, at: Before, checkedByVersion: "1")).ToList();
        var current = Enumerable.Range(0, 400).Select(i => Settled(i, checkedByVersion: "1")).ToList();
        var sentUp = new List<SentUpRecheck> { Up(0, checkedByVersion: "2") };

        WindowCloseResult result;
        if (throughTheCloser)
        {
            var rechecks = new InMemoryRecheckStore();
            var sampled = new InMemorySentUpRecheckStore();
            foreach (var r in previous.Concat(current))
                await rechecks.AppendAsync(r);
            foreach (var r in sentUp)
                await sampled.AppendAsync(r);
            var state = new InMemoryWindowCloseState();
            await state.SaveAsync(At(TrustState.Stable));
            result = await new WindowCloser(rechecks, new InMemoryPlantStore(), state, sampled).CloseAsync(Input(Sampled()));
        }
        else
        {
            result = WindowClose.Run(Input(Sampled()), Records(current, sentUp, previous), At(TrustState.Stable));
        }

        Assert.Equal(TrustState.Active, Assert.Single(result.Step.Slices).Step.To);

        // The same version on both sides of the window is not a change, wherever it was seen.
        var same = WindowClose.Run(Input(Sampled()), Records(current, sentUp, previous, [Up(0, Before, checkedByVersion: "2")]), At(TrustState.Stable));
        Assert.Equal(TrustState.Stable, Assert.Single(same.Step.Slices).Step.To);
    }

    [Fact]
    public async Task TheSentUpStores_KeepOneRecordPerDecisionAndTier_AndReadByDecisionTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"honeai-sentup-{Guid.NewGuid():N}.jsonl");
        try
        {
            foreach (var store in new ISentUpRecheckStore[] { new InMemorySentUpRecheckStore(), new JsonlSentUpRecheckStore(path) })
            {
                var record = Up(0) with { UpperFailed = true, CheckedBy = "people", CheckedByVersion = "3", Blind = false };
                Assert.True(await store.AppendAsync(record));
                Assert.False(await store.AppendAsync(record with { UpperFailed = false }));
                Assert.True(await store.AppendAsync(Up(1, at: Before)));
                Assert.True(await store.AppendAsync(Up(2, slice: "other")));

                var read = new List<SentUpRecheck>();
                await foreach (var r in store.ReadAsync("ml", "s", Now - Window, Now))
                    read.Add(r);

                Assert.Equal(record, Assert.Single(read));
            }

            // A second store on the same file reads what the first wrote and refuses the same key again.
            var reopened = new JsonlSentUpRecheckStore(path);
            Assert.False(await reopened.AppendAsync(Up(0)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
