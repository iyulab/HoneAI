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
/// Closes a window against the stores: reads the recheck and plant records of the closed window and of the stretch before it, loads
/// the state, runs <see cref="WindowClose.Run"/> and keeps the state it returns. When to call it, and what to do with the snapshot, the
/// awaiting releases and the retrain decisions, is the consumer's — a schedule, an HTTP host, a person at a prompt.
/// </summary>
/// <remarks>
/// One close at a time per state: two calls at once read the same state and the later save wins. A release that waits for approval is
/// committed by the caller once approved, by saving a state whose standing for it is <see cref="StandingChange.Standing"/>
/// (<see cref="ControllerStep.AwaitingApproval"/> lists them).
/// </remarks>
public sealed class WindowCloser
{
    private readonly IRecheckStore _rechecks;
    private readonly IPlantStore _plants;
    private readonly IWindowCloseState _state;

    /// <summary>Creates a closer over the stores.</summary>
    public WindowCloser(IRecheckStore rechecks, IPlantStore plants, IWindowCloseState state)
    {
        _rechecks = rechecks ?? throw new ArgumentNullException(nameof(rechecks));
        _plants = plants ?? throw new ArgumentNullException(nameof(plants));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Closes the window of <paramref name="input"/> and keeps the state.</summary>
    public async ValueTask<WindowCloseResult> CloseAsync(WindowCloseInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(input), input.Window, "The window has a length.");
        var from = input.Now - input.Window;
        var before = from - input.Window;

        var rechecks = new List<RecheckRecord>();
        var previous = new List<RecheckRecord>();
        foreach (var cell in input.Slices.Select(s => (s.TierId, s.SliceKey)).Distinct())
        {
            await foreach (var record in _rechecks.ReadAsync(cell.TierId, cell.SliceKey, from, input.Now, cancellationToken).ConfigureAwait(false))
                rechecks.Add(record);
            await foreach (var record in _rechecks.ReadAsync(cell.TierId, cell.SliceKey, before, from, cancellationToken).ConfigureAwait(false))
                previous.Add(record);
        }
        var plants = new List<PlantRecord>();
        foreach (var teacher in input.Slices.Where(s => s.TeacherTierId is not null).Select(s => s.TeacherTierId!).Distinct(StringComparer.Ordinal))
        {
            await foreach (var record in _plants.ReadAsync(teacher, from, input.Now, cancellationToken).ConfigureAwait(false))
                plants.Add(record);
        }

        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = WindowClose.Run(input, new WindowRecords(rechecks, previous, plants), state);
        await _state.SaveAsync(result.State, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
