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
    /// <remarks>Applies the same read rules as <see cref="ReadFromAsync(TextReader, string?, CancellationToken)"/>.</remarks>
    public async IAsyncEnumerable<ProvenanceRecord> ReadAsync(
        string? subjectId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            yield break;

        using var reader = new StreamReader(_path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        await foreach (var record in ReadFromAsync(reader, subjectId, cancellationToken).ConfigureAwait(false))
            yield return record;
    }

    /// <summary>
    /// Reads provenance JSONL from any stream — a downloaded blob, a database column, an HTTP
    /// response — with the same rules as a sink's own file: lines written by 0.2.x read as the
    /// current contract, and a line cut short by a crash mid-append is skipped.
    /// </summary>
    /// <param name="stream">UTF-8 JSONL. Read to its end; left open (the caller owns it).</param>
    /// <param name="subjectId">When set, only records for this subject.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async IAsyncEnumerable<ProvenanceRecord> ReadFromAsync(
        Stream stream, string? subjectId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        await foreach (var record in ReadFromAsync(reader, subjectId, cancellationToken).ConfigureAwait(false))
            yield return record;
    }

    /// <summary>
    /// Reads provenance JSONL from text already in hand (wrap a string in a <see cref="StringReader"/>),
    /// with the same rules as a sink's own file: lines written by 0.2.x read as the current contract,
    /// and a line cut short by a crash mid-append is skipped.
    /// </summary>
    /// <param name="reader">JSONL text. Read to its end; left open (the caller owns it).</param>
    /// <param name="subjectId">When set, only records for this subject.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async IAsyncEnumerable<ProvenanceRecord> ReadFromAsync(
        TextReader reader, string? subjectId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (ParseLine(line) is not { } record)
                continue;
            if (subjectId is not null && record.SubjectId != subjectId)
                continue;

            yield return record;
        }
    }

    private static ProvenanceRecord? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            // A line cut short by a crash mid-append: the record it carried was never written.
            return null;
        }
        if (node is JsonObject { } obj && obj["provenance"] is JsonObject provenance)
            UpgradeLegacyProvenance(provenance);
        return node.Deserialize<ProvenanceRecord>(JsonOptions);
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
