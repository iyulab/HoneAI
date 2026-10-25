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
    private readonly ConcurrentDictionary<(string, string, PoolPurpose, string), PoolResponse> _responses = new();

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
        return ValueTask.FromResult(_responses.TryAdd(PoolResponseKeys.Of(response), response));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PoolEntry> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in _items.Values.Where(i => i.DecidedAt >= from && i.DecidedAt < to).OrderBy(i => i.DecidedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (item.DecisionId, item.TierId);
            var responses = _responses.Values.Where(r => r.DecisionId == item.DecisionId && r.TierId == item.TierId).ToList();
            yield return PoolResponseKeys.Entry(item, _leases.GetValueOrDefault(key), responses);
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
    private readonly JsonlKeyedLog<PoolResponse, (string, string, PoolPurpose, string)> _responses;
    private readonly string _leases;

    /// <param name="directory">Where <c>items.jsonl</c>, <c>leases.jsonl</c> and <c>responses.jsonl</c> live.</param>
    public JsonlRecheckPoolStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _items = new(Path.Combine(directory, "items.jsonl"), i => (i.DecisionId, i.TierId));
        _responses = new(Path.Combine(directory, "responses.jsonl"), PoolResponseKeys.Of);
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
    {
        PoolResponseKeys.Of(response);
        return _responses.AppendAsync(response, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PoolEntry> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var leases = new Dictionary<(string, string), PoolLease>();
        await foreach (var lease in JsonlFile.ReadAsync<PoolLease>(_leases, cancellationToken).ConfigureAwait(false))
            leases[(lease.DecisionId, lease.TierId)] = lease;
        var responses = new Dictionary<(string, string), List<PoolResponse>>();
        await foreach (var response in _responses.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (response.DecisionId, response.TierId);
            if (!responses.TryGetValue(key, out var list))
                responses[key] = list = [];
            list.Add(response);
        }
        await foreach (var item in _items.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.DecidedAt < from || item.DecidedAt >= to)
                continue;
            var key = (item.DecisionId, item.TierId);
            yield return PoolResponseKeys.Entry(item, leases.GetValueOrDefault(key), responses.GetValueOrDefault(key) ?? []);
        }
    }
}

// A recheck is answered once per item; a plant, repeat or second opinion once per judge.
internal static class PoolResponseKeys
{
    public static (string, string, PoolPurpose, string) Of(PoolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Purpose == PoolPurpose.Recheck)
            return (response.DecisionId, response.TierId, response.Purpose, string.Empty);
        if (string.IsNullOrEmpty(response.ReviewerKey))
            throw new ArgumentException($"A {response.Purpose} response is recorded per judge: it needs a reviewer key.", nameof(response));
        return (response.DecisionId, response.TierId, response.Purpose, response.ReviewerKey);
    }

    public static PoolEntry Entry(PoolItem item, PoolLease? lease, IReadOnlyCollection<PoolResponse> responses)
    {
        var recheck = responses.FirstOrDefault(r => r.Purpose == PoolPurpose.Recheck);
        var others = responses.Where(r => r.Purpose != PoolPurpose.Recheck).ToList();
        return new PoolEntry(item, lease, recheck, others.Count == 0 ? null : others);
    }
}
