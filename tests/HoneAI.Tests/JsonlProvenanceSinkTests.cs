using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Tests for <see cref="JsonlProvenanceSink"/> against a real temp file — append-only
/// round-trip, subject filtering, deterministic stamping (injected clock), and
/// audit-readable enum serialization.
/// </summary>
public sealed class JsonlProvenanceSinkTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"honeai-prov-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static PredictionProvenance Prov(ReasoningLayer layer, double confidence, bool review = false)
        => new() { SourceLayer = layer, Confidence = confidence, RequiresReview = review };

    [Fact]
    public async Task Append_ThenRead_RoundTripsRecords()
    {
        var sink = new JsonlProvenanceSink(_path);

        await sink.AppendAsync("item-1", Prov(ReasoningLayer.AutoMl, 0.8));
        await sink.AppendAsync("item-2", Prov(ReasoningLayer.Frontier, 0.3, review: true));

        var all = await ReadAll(sink);

        Assert.Equal(2, all.Count);
        Assert.Equal("item-1", all[0].SubjectId);
        Assert.Equal(ReasoningLayer.AutoMl, all[0].Provenance.SourceLayer);
        Assert.Equal(0.8, all[0].Provenance.Confidence);
        Assert.True(all[1].Provenance.RequiresReview);
    }

    [Fact]
    public async Task Read_FiltersBySubject()
    {
        var sink = new JsonlProvenanceSink(_path);
        await sink.AppendAsync("a", Prov(ReasoningLayer.AutoMl, 0.5));
        await sink.AppendAsync("b", Prov(ReasoningLayer.Statistics, 0.6));
        await sink.AppendAsync("a", Prov(ReasoningLayer.Frontier, 0.7));

        var onlyA = await ReadAll(sink, "a");

        Assert.Equal(2, onlyA.Count);
        Assert.All(onlyA, r => Assert.Equal("a", r.SubjectId));
    }

    [Fact]
    public async Task Append_StampsWithInjectedClock()
    {
        var fixedTime = new DateTimeOffset(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);
        var sink = new JsonlProvenanceSink(_path, () => fixedTime);

        var record = await sink.AppendAsync("x", Prov(ReasoningLayer.Theory, 1.0));

        Assert.Equal(fixedTime, record.RecordedAt);
        var read = await ReadAll(sink);
        Assert.Equal(fixedTime, read[0].RecordedAt);
    }

    [Fact]
    public async Task SerializedLine_UsesReadableLayerName()
    {
        var sink = new JsonlProvenanceSink(_path);
        await sink.AppendAsync("x", Prov(ReasoningLayer.AutoMl, 0.9));

        var raw = await File.ReadAllTextAsync(_path);
        Assert.Contains("\"AutoMl\"", raw);        // enum as name, not "2"
        Assert.DoesNotContain("\"sourceLayer\":2", raw);
    }

    [Fact]
    public async Task Read_OnMissingFile_YieldsNothing()
    {
        var sink = new JsonlProvenanceSink(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.jsonl"));
        Assert.Empty(await ReadAll(sink));
    }

    [Fact]
    public async Task Append_PreservesAnnotations()
    {
        var sink = new JsonlProvenanceSink(_path);
        var prov = new PredictionProvenance
        {
            SourceLayer = ReasoningLayer.AutoMl,
            Confidence = 0.7,
            Annotations = new Dictionary<string, string> { ["category"] = "Security" },
        };
        await sink.AppendAsync("x", prov);

        var read = await ReadAll(sink);
        Assert.Equal("Security", read[0].Provenance.Annotations!["category"]);
    }

    private static async Task<List<ProvenanceRecord>> ReadAll(IProvenanceSink sink, string? subject = null)
    {
        var list = new List<ProvenanceRecord>();
        await foreach (var r in sink.ReadAsync(subject))
            list.Add(r);
        return list;
    }
}
