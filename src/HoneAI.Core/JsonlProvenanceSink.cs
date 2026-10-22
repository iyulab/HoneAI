using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// File-backed <see cref="IProvenanceSink"/> that appends each assessment as one JSON
/// line (JSONL). Append-only and Git-friendly; reasoning layers serialize as readable
/// names for audit.
/// </summary>
/// <remarks>
/// Appends are serialized within this process via an async lock; the sink does not
/// coordinate across processes (one writer per file is assumed).
/// </remarks>
public sealed class JsonlProvenanceSink : IProvenanceSink
{
    private static readonly JsonSerializerOptions JsonOptions = JsonlFile.Options;

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    /// <param name="path">JSONL file to append to (created on first write, with parent directories).</param>
    /// <param name="clock">Time source for record stamping; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public JsonlProvenanceSink(string path, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<ProvenanceRecord> AppendAsync(
        string subjectId, PredictionProvenance provenance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentNullException.ThrowIfNull(provenance);

        var record = new ProvenanceRecord(subjectId, provenance, _clock());

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonlFile.AppendAsync(_path, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _appendLock.Release();
        }

        return record;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProvenanceRecord> ReadAsync(
        string? subjectId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            yield break;

        await foreach (var line in File.ReadLinesAsync(_path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                // A line cut short by a crash mid-append: the record it carried was never written.
                continue;
            }
            if (node is JsonObject { } obj && obj["provenance"] is JsonObject provenance)
                UpgradeLegacyProvenance(provenance);
            var record = node.Deserialize<ProvenanceRecord>(JsonOptions);
            if (record is null)
                continue;
            if (subjectId is not null && record.SubjectId != subjectId)
                continue;

            yield return record;
        }
    }

    // Kind names that changed when the reasoning layers became judge kinds. Numbers did not move.
    private static readonly Dictionary<string, string> LegacyKindNames = new(StringComparer.Ordinal)
    {
        ["AutoMl"] = nameof(JudgeKind.Ml),
        ["Frontier"] = nameof(JudgeKind.Llm),
    };

    /// <summary>
    /// Reads a record written by 0.2.x as the current contract. An audit trail is never rewritten,
    /// so the old shape is a read rule, not an API alias: <c>sourceLayer</c> becomes <c>kind</c>
    /// (with the renamed values mapped), and the two flags the per-tier path replaced are kept as
    /// <c>legacy:</c> annotations rather than dropped.
    /// </summary>
    private static void UpgradeLegacyProvenance(JsonObject provenance)
    {
        if (provenance["sourceLayer"] is { } layer && provenance["kind"] is null)
        {
            provenance.Remove("sourceLayer");
            provenance["kind"] = layer.GetValueKind() == JsonValueKind.String
                && LegacyKindNames.TryGetValue(layer.GetValue<string>(), out var renamed)
                ? renamed
                : layer.DeepClone();
        }

        foreach (var flag in new[] { "agreement", "requiresReview" })
        {
            if (provenance[flag] is not { } value)
                continue;
            provenance.Remove(flag);
            if (provenance["annotations"] is not JsonObject annotations)
                provenance["annotations"] = annotations = new JsonObject();
            annotations["legacy:" + flag] = value.ToJsonString();
        }
    }
}
