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

    private static PredictionProvenance Prov(JudgeKind kind, double confidence, bool unverified = false)
        => new() { Kind = kind, Confidence = confidence, Unverified = unverified };

    [Fact]
    public async Task Append_ThenRead_RoundTripsRecords()
    {
        var sink = new JsonlProvenanceSink(_path);

        await sink.AppendAsync("item-1", Prov(JudgeKind.Ml, 0.8));
        await sink.AppendAsync("item-2", Prov(JudgeKind.Llm, 0.3, unverified: true));

        var all = await ReadAll(sink);

        Assert.Equal(2, all.Count);
        Assert.Equal("item-1", all[0].SubjectId);
        Assert.Equal(JudgeKind.Ml, all[0].Provenance.Kind);
        Assert.Equal(0.8, all[0].Provenance.Confidence);
        Assert.True(all[1].Provenance.Unverified);
    }

    [Fact]
    public async Task Read_FiltersBySubject()
    {
        var sink = new JsonlProvenanceSink(_path);
        await sink.AppendAsync("a", Prov(JudgeKind.Ml, 0.5));
        await sink.AppendAsync("b", Prov(JudgeKind.Statistics, 0.6));
        await sink.AppendAsync("a", Prov(JudgeKind.Llm, 0.7));

        var onlyA = await ReadAll(sink, "a");

        Assert.Equal(2, onlyA.Count);
        Assert.All(onlyA, r => Assert.Equal("a", r.SubjectId));
    }

    [Fact]
    public async Task Append_StampsWithInjectedClock()
    {
        var fixedTime = new DateTimeOffset(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);
        var sink = new JsonlProvenanceSink(_path, () => fixedTime);

        var record = await sink.AppendAsync("x", Prov(JudgeKind.Theory, 1.0));

        Assert.Equal(fixedTime, record.RecordedAt);
        var read = await ReadAll(sink);
        Assert.Equal(fixedTime, read[0].RecordedAt);
    }

    [Fact]
    public async Task SerializedLine_UsesReadableKindName()
    {
        var sink = new JsonlProvenanceSink(_path);
        await sink.AppendAsync("x", Prov(JudgeKind.Ml, 0.9));

        var raw = await File.ReadAllTextAsync(_path);
        Assert.Contains("\"kind\":\"Ml\"", raw);   // enum as name, not "2"
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
            Kind = JudgeKind.Ml,
            Confidence = 0.7,
            Annotations = new Dictionary<string, string> { ["category"] = "Security" },
        };
        await sink.AppendAsync("x", prov);

        var read = await ReadAll(sink);
        Assert.Equal("Security", read[0].Provenance.Annotations!["category"]);
    }

    [Fact]
    public async Task Read_ALineWrittenBy02x_ReadsAsTheCurrentContract()
    {
        // An audit trail is never rewritten: a 0.2.x line keeps reading, renamed kinds mapped and the
        // two flags the per-tier path replaced kept as annotations.
        await File.WriteAllTextAsync(_path,
            "{\"subjectId\":\"old\",\"provenance\":{\"sourceLayer\":\"Frontier\",\"confidence\":0.4,\"agreement\":false,\"requiresReview\":true,\"annotations\":{\"category\":\"Security\"}},\"recordedAt\":\"2026-09-01T00:00:00+00:00\"}\n"
            + "{\"subjectId\":\"old2\",\"provenance\":{\"sourceLayer\":\"AutoMl\",\"confidence\":0.9},\"recordedAt\":\"2026-09-01T00:00:00+00:00\"}\n");

        var read = await ReadAll(new JsonlProvenanceSink(_path));

        Assert.Equal(JudgeKind.Llm, read[0].Provenance.Kind);
        Assert.Equal(0.4, read[0].Provenance.Confidence);
        Assert.Equal("false", read[0].Provenance.Annotations!["legacy:agreement"]);
        Assert.Equal("true", read[0].Provenance.Annotations!["legacy:requiresReview"]);
        Assert.Equal("Security", read[0].Provenance.Annotations!["category"]);
        Assert.Equal(JudgeKind.Ml, read[1].Provenance.Kind);
        Assert.Null(read[1].Provenance.Annotations);
    }

    private static async Task<List<ProvenanceRecord>> ReadAll(IProvenanceSink sink, string? subject = null)
    {
        var list = new List<ProvenanceRecord>();
        await foreach (var r in sink.ReadAsync(subject))
            list.Add(r);
        return list;
    }
}
