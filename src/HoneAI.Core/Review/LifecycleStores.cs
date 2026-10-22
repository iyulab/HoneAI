using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary><see cref="ILifecycleStore"/> in this process's memory — runs that need not outlive it.</summary>
public sealed class InMemoryLifecycleStore : ILifecycleStore
{
    private readonly ConcurrentDictionary<string, LifecycleRun> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(LifecycleRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs[run.RunId] = run;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LifecycleRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);
}

/// <summary>
/// File-backed <see cref="ILifecycleStore"/>: every save appends the run as one JSON line, and the
/// last line of an id is the run. Runs survive restarts; the file is also the run's history.
/// </summary>
/// <remarks>A reference implementation — each read scans the file. Operational storage is a row per run in the consumer's store.</remarks>
public sealed class JsonlLifecycleStore : ILifecycleStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="path">The JSONL file (created on first save, with parent directories).</param>
    public JsonlLifecycleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public async Task SaveAsync(LifecycleRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonlFile.AppendAsync(_path, run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<LifecycleRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        LifecycleRun? last = null;
        await foreach (var run in JsonlFile.ReadAsync<LifecycleRun>(_path, cancellationToken).ConfigureAwait(false))
        {
            if (run.RunId == runId)
                last = run;
        }
        return last;
    }
}
