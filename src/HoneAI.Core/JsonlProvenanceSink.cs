using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// File-backed <see cref="IProvenanceSink"/> that appends each assessment as one JSON
/// line (JSONL) — the generic form of U-Vision's <c>FileMetricsStore</c>
/// (back-derivation §3.5 ②). Append-only and Git-friendly; reasoning layers serialize as
/// readable names for audit.
/// </summary>
/// <remarks>
/// Appends are serialized within this process via an async lock; the sink does not
/// coordinate across processes (one writer per file is assumed, as in the consumers).
/// </remarks>
public sealed class JsonlProvenanceSink : IProvenanceSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
        var line = JsonSerializer.Serialize(record, JsonOptions);

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
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

            var record = JsonSerializer.Deserialize<ProvenanceRecord>(line, JsonOptions);
            if (record is null)
                continue;
            if (subjectId is not null && record.SubjectId != subjectId)
                continue;

            yield return record;
        }
    }
}
