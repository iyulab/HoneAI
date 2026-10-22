using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// <see cref="IReviewStore"/> behaviour, held by both reference implementations: open and decide are
/// idempotent by review id, the first decision stands, and the pending list is what is still undecided.
/// </summary>
public sealed class ReviewStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "honeai-reviews-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    public static TheoryData<string> Stores => ["memory", "jsonl"];

    private IReviewStore Store(string kind, Func<DateTimeOffset>? clock = null) => kind switch
    {
        "memory" => new InMemoryReviewStore(clock),
        _ => new JsonlReviewStore(Path.Combine(_dir, "reviews.jsonl"), clock),
    };

    [Theory, MemberData(nameof(Stores))]
    public async Task Open_IsIdempotent_AndDecide_KeepsTheFirstDecision(string kind)
    {
        var store = Store(kind);

        var opened = await store.OpenAsync("rev-1", "run-1");
        var again = await store.OpenAsync("rev-1", "another subject");
        var first = await store.DecideAsync("rev-1", new ReviewDecision("approve", ReviewerKey: "r-1"));
        var second = await store.DecideAsync("rev-1", new ReviewDecision("reject"));

        Assert.Equal(opened, again);                                   // same review, subject unchanged
        Assert.Equal("approve", first!.Decision!.Verdict);
        Assert.Equal("approve", second!.Decision!.Verdict);             // the first decision stands
        Assert.Equal("r-1", (await store.GetAsync("rev-1"))!.Decision!.ReviewerKey);
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Decide_OnAnUnknownReview_IsNull(string kind)
    {
        var store = Store(kind);

        Assert.Null(await store.DecideAsync("nope", new ReviewDecision("approve")));
        Assert.Null(await store.GetAsync("nope"));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Pending_ListsTheUndecided_OldestFirst(string kind)
    {
        var t = DateTimeOffset.UnixEpoch;
        var store = Store(kind, () => t = t.AddMinutes(1));
        await store.OpenAsync("b");
        await store.OpenAsync("a");
        await store.OpenAsync("c");
        await store.DecideAsync("a", new ReviewDecision("approve"));

        var pending = await store.ListPendingAsync();

        Assert.Equal(new[] { "b", "c" }, pending.Select(r => r.ReviewId));
    }

    [Fact]
    public async Task Jsonl_IsSharedBetweenInstances_AndSurvivesATornLastLine()
    {
        var path = Path.Combine(_dir, "shared.jsonl");
        await new JsonlReviewStore(path).OpenAsync("rev-1");
        await File.AppendAllTextAsync(path, "{\"kind\":\"decided\",\"revi");    // a crash mid-append

        var decided = await new JsonlReviewStore(path).DecideAsync("rev-1", new ReviewDecision("approve"));

        Assert.Equal("approve", decided!.Decision!.Verdict);
        Assert.True((await new JsonlReviewStore(path).GetAsync("rev-1"))!.IsDecided);
    }

    [Fact]
    public async Task WaitForDecision_ReturnsOnceAnotherWriterDecides()
    {
        var store = new InMemoryReviewStore();
        var waiting = store.WaitForDecisionAsync("rev-1", TimeSpan.FromMilliseconds(5));
        await store.DecideAsync("rev-1", new ReviewDecision("approve"));      // too early: not open yet
        while ((await store.GetAsync("rev-1")) is null)
            await Task.Delay(5);
        Assert.False(waiting.IsCompleted);

        await store.DecideAsync("rev-1", new ReviewDecision("approve"));

        Assert.Equal("approve", (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Verdict);
    }
}
