using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Trust;

/// <summary>An <see cref="IRecheckPoolStore"/> in this process's memory — for tests and single-process consumers.</summary>
public sealed class InMemoryRecheckPoolStore : IRecheckPoolStore
{
    private readonly ConcurrentDictionary<(string, string), PoolItem> _items = new();
    private readonly ConcurrentDictionary<(string, string), PoolLease> _leases = new();
    private readonly ConcurrentDictionary<(string, string), PoolResponse> _responses = new();

    /// <inheritdoc />
    public ValueTask<bool> AddAsync(PoolItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ValueTask.FromResult(_items.TryAdd((item.DecisionId, item.TierId), item));
    }

    /// <inheritdoc />
    public ValueTask LeaseAsync(PoolLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _leases[(lease.DecisionId, lease.TierId)] = lease;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RespondAsync(PoolResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ValueTask.FromResult(_responses.TryAdd((response.DecisionId, response.TierId), response));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PoolEntry> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in _items.Values.Where(i => i.DecidedAt >= from && i.DecidedAt < to).OrderBy(i => i.DecidedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (item.DecisionId, item.TierId);
            yield return new PoolEntry(item, _leases.GetValueOrDefault(key), _responses.GetValueOrDefault(key));
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// An <see cref="IRecheckPoolStore"/> over three append-only JSONL files in one directory — items and responses
/// written once per key, holds appended and the latest one standing. One writer process per directory.
/// </summary>
public sealed class JsonlRecheckPoolStore : IRecheckPoolStore
{
    private readonly JsonlKeyedLog<PoolItem, (string, string)> _items;
    private readonly JsonlKeyedLog<PoolResponse, (string, string)> _responses;
    private readonly string _leases;

    /// <param name="directory">Where <c>items.jsonl</c>, <c>leases.jsonl</c> and <c>responses.jsonl</c> live.</param>
    public JsonlRecheckPoolStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _items = new(Path.Combine(directory, "items.jsonl"), i => (i.DecisionId, i.TierId));
        _responses = new(Path.Combine(directory, "responses.jsonl"), r => (r.DecisionId, r.TierId));
        _leases = Path.Combine(directory, "leases.jsonl");
    }

    /// <inheritdoc />
    public ValueTask<bool> AddAsync(PoolItem item, CancellationToken cancellationToken = default) => _items.AppendAsync(item, cancellationToken);

    /// <inheritdoc />
    public async ValueTask LeaseAsync(PoolLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await JsonlFile.AppendAsync(_leases, lease, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<bool> RespondAsync(PoolResponse response, CancellationToken cancellationToken = default)
        => _responses.AppendAsync(response, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<PoolEntry> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var leases = new Dictionary<(string, string), PoolLease>();
        await foreach (var lease in JsonlFile.ReadAsync<PoolLease>(_leases, cancellationToken).ConfigureAwait(false))
            leases[(lease.DecisionId, lease.TierId)] = lease;
        var responses = new Dictionary<(string, string), PoolResponse>();
        await foreach (var response in _responses.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            responses.TryAdd((response.DecisionId, response.TierId), response);
        await foreach (var item in _items.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.DecidedAt < from || item.DecidedAt >= to)
                continue;
            var key = (item.DecisionId, item.TierId);
            yield return new PoolEntry(item, leases.GetValueOrDefault(key), responses.GetValueOrDefault(key));
        }
    }
}
