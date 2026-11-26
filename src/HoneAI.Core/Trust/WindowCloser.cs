using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Trust;

/// <summary>Where the driver keeps what it carries between windows (<see cref="WindowCloseState"/>).</summary>
public interface IWindowCloseState
{
    /// <summary>The state kept at the last close; <see cref="WindowCloseState.Empty"/> before the first.</summary>
    ValueTask<WindowCloseState> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Keeps <paramref name="state"/>, replacing what was kept.</summary>
    ValueTask SaveAsync(WindowCloseState state, CancellationToken cancellationToken = default);
}

/// <summary>A <see cref="IWindowCloseState"/> in memory — for a host that lives as long as its windows, and for tests.</summary>
public sealed class InMemoryWindowCloseState : IWindowCloseState
{
    private WindowCloseState _state = WindowCloseState.Empty;

    /// <inheritdoc />
    public ValueTask<WindowCloseState> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Volatile.Read(ref _state));

    /// <inheritdoc />
    public ValueTask SaveAsync(WindowCloseState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Volatile.Write(ref _state, state);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Closes a window against the stores: reads the recheck records (and the sampled judgments of sent-up decisions, when given a
/// store of them) of the closed window and of the stretch before it, and the plant records of the closed window, loads
/// the state, runs <see cref="WindowClose.Run"/> and keeps the state it returns. When to call it, and what to do with the snapshot, the
/// awaiting releases and the retrain decisions, is the consumer's — a schedule, an HTTP host, a person at a prompt.
/// </summary>
/// <remarks>
/// One close at a time per state: two calls at once read the same state and the later save wins. A release that waits for approval
/// (<see cref="ControllerStep.AwaitingApproval"/>) is committed once a person approved it, by <see cref="ApproveAsync"/>.
/// </remarks>
public sealed class WindowCloser
{
    private readonly IRecheckStore _rechecks;
    private readonly IPlantStore _plants;
    private readonly IWindowCloseState _state;
    private readonly ISentUpRecheckStore? _sentUp;

    /// <summary>Creates a closer over the stores, for slices whose sent-up decisions the tier above judges every one of.</summary>
    public WindowCloser(IRecheckStore rechecks, IPlantStore plants, IWindowCloseState state)
    {
        _rechecks = rechecks ?? throw new ArgumentNullException(nameof(rechecks));
        _plants = plants ?? throw new ArgumentNullException(nameof(plants));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    /// Creates a closer that also reads the sampled judgments of sent-up decisions — what a slice that declares
    /// <see cref="WindowSlice.SentUpSampled"/> is closed on. It reads them for every slice it closes, so that judgments
    /// recorded for a slice that does not declare a sample are refused (<see cref="WindowClose.Run"/>).
    /// </summary>
    public WindowCloser(IRecheckStore rechecks, IPlantStore plants, IWindowCloseState state, ISentUpRecheckStore sentUp)
        : this(rechecks, plants, state)
    {
        _sentUp = sentUp ?? throw new ArgumentNullException(nameof(sentUp));
    }

    /// <summary>
    /// Keeps the standing a release would become, after a person approved it (<see cref="WindowCloseState.Approve"/> — refused when the
    /// release is no longer waiting or the slice has moved on). Returns the state kept.
    /// </summary>
    public async ValueTask<WindowCloseState> ApproveAsync(SliceOutcome waiting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        var approved = (await _state.LoadAsync(cancellationToken).ConfigureAwait(false)).Approve(waiting);
        await _state.SaveAsync(approved, cancellationToken).ConfigureAwait(false);
        return approved;
    }

    /// <summary>Keeps that a person declined <paramref name="waiting"/> (<see cref="WindowCloseState.Reject"/>): the same release is not put forward again while the evidence keeps proposing it.</summary>
    public async ValueTask<WindowCloseState> RejectAsync(SliceOutcome waiting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        var rejected = (await _state.LoadAsync(cancellationToken).ConfigureAwait(false)).Reject(waiting);
        await _state.SaveAsync(rejected, cancellationToken).ConfigureAwait(false);
        return rejected;
    }

    /// <summary>Closes the window of <paramref name="input"/> and keeps the state.</summary>
    public async ValueTask<WindowCloseResult> CloseAsync(WindowCloseInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(input), input.Window, "The window has a length.");
        if (_sentUp is null && input.Slices.FirstOrDefault(s => s.SentUpSampled) is { } unread)
            throw new InvalidOperationException(
                $"Tier '{unread.TierId}' in slice '{unread.SliceKey}' declares {nameof(WindowSlice.SentUpSampled)}, and this closer has no " +
                $"{nameof(ISentUpRecheckStore)} to read the sample from.");
        var from = input.Now - input.Window;
        var before = from - input.Window;

        var rechecks = new List<RecheckRecord>();
        var previous = new List<RecheckRecord>();
        var sentUp = new List<SentUpRecheck>();
        var previousSentUp = new List<SentUpRecheck>();
        foreach (var cell in input.Slices.Select(s => (s.TierId, s.SliceKey)).Distinct())
        {
            await foreach (var record in _rechecks.ReadAsync(cell.TierId, cell.SliceKey, from, input.Now, cancellationToken).ConfigureAwait(false))
                rechecks.Add(record);
            await foreach (var record in _rechecks.ReadAsync(cell.TierId, cell.SliceKey, before, from, cancellationToken).ConfigureAwait(false))
                previous.Add(record);
            if (_sentUp is null)
                continue;
            await foreach (var record in _sentUp.ReadAsync(cell.TierId, cell.SliceKey, from, input.Now, cancellationToken).ConfigureAwait(false))
                sentUp.Add(record);
            await foreach (var record in _sentUp.ReadAsync(cell.TierId, cell.SliceKey, before, from, cancellationToken).ConfigureAwait(false))
                previousSentUp.Add(record);
        }
        var plants = new List<PlantRecord>();
        foreach (var teacher in input.Slices.Where(s => s.TeacherTierId is not null).Select(s => s.TeacherTierId!).Distinct(StringComparer.Ordinal))
        {
            await foreach (var record in _plants.ReadAsync(teacher, from, input.Now, cancellationToken).ConfigureAwait(false))
                plants.Add(record);
        }

        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        var records = new WindowRecords(rechecks, previous, plants) { SentUp = sentUp, PreviousSentUp = previousSentUp };
        var result = WindowClose.Run(input, records, state);
        await _state.SaveAsync(result.State, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
