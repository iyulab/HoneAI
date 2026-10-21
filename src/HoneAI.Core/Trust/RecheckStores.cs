using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Trust;

/// <summary>In-memory <see cref="IRecheckStore"/> — for tests, examples and a single process.</summary>
public sealed class InMemoryRecheckStore : IRecheckStore
{
    private readonly ConcurrentDictionary<(string DecisionId, string TierId), RecheckRecord> _records = new();

    /// <inheritdoc />
    public ValueTask<bool> AppendAsync(RecheckRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ValueTask.FromResult(_records.TryAdd((record.DecisionId, record.TierId), record));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecheckRecord> ReadAsync(
        string tierId, string sliceKey, DateTimeOffset from, DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in _records.Values
                     .Where(r => r.TierId == tierId && r.SliceKey == sliceKey && r.DecidedAt >= from && r.DecidedAt < to)
                     .OrderBy(r => r.DecidedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// File-backed <see cref="IRecheckStore"/>: one JSON line per recheck, append-only. A record for a
/// decision and tier already in the file is not written again.
/// </summary>
/// <remarks>
/// One writer per file within one process is assumed (appends are serialized by an async lock;
/// processes do not coordinate). The keys already written are read once, on the first append.
/// </remarks>
public sealed class JsonlRecheckStore : IRecheckStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HashSet<(string, string)>? _keys;

    /// <param name="path">JSONL file (created on first write, with parent directories).</param>
    public JsonlRecheckStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public async ValueTask<bool> AppendAsync(RecheckRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is null)
            {
                _keys = [];
                await foreach (var existing in ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    _keys.Add((existing.DecisionId, existing.TierId));
            }
            if (!_keys.Add((record.DecisionId, record.TierId)))
                return false;

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine,
                Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecheckRecord> ReadAsync(
        string tierId, string sliceKey, DateTimeOffset from, DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var record in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.TierId == tierId && record.SliceKey == sliceKey && record.DecidedAt >= from && record.DecidedAt < to)
                yield return record;
        }
    }

    private async IAsyncEnumerable<RecheckRecord> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            yield break;
        await foreach (var line in File.ReadLinesAsync(_path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (JsonSerializer.Deserialize<RecheckRecord>(line, JsonOptions) is { } record)
                yield return record;
        }
    }
}
