using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>How a drawn probe left its adapter.</summary>
public enum ProbeDeliveryKind
{
    /// <summary>The judge answered on the spot: the verdict is on the delivery.</summary>
    Judged = 0,

    /// <summary>
    /// Set aside for a judge that comes when it comes (a pool, a queue). The answer arrives later, and an item the judge
    /// never reaches is counted as not reached (<see cref="RecheckPool.Account"/>), not as failed.
    /// </summary>
    Queued = 1,

    /// <summary>
    /// Drawn but not put: no adapter takes its target and method. It was drawn at π, so it is a probe the measurement
    /// owes — count it as not reached, never drop it from the denominator.
    /// </summary>
    Declined = 2,
}

/// <summary>What became of one drawn probe.</summary>
/// <param name="Probe">The probe, with the π it was drawn at — the method's own denominator.</param>
/// <param name="Kind">How it left.</param>
/// <param name="Result">The verdict, when <paramref name="Kind"/> is <see cref="ProbeDeliveryKind.Judged"/>. What it is for follows the method: a recheck, a double check, a repeat, a plant or an anchoring pair are told apart by <c>Probe.Candidate.Method</c>.</param>
/// <param name="Reason">Why, when it was not judged on the spot.</param>
public sealed record ProbeDelivery<TValue>(Probe Probe, ProbeDeliveryKind Kind, RecheckResult<TValue>? Result = null, string? Reason = null);

/// <summary>
/// How one kind of judge is asked. The selection (<see cref="ProbeSelector"/>) is the same for every judge; what
/// differs is the way a probe is put — a call now, a card in a queue — and that is all an adapter is. The
/// <em>capacity</em> of a judge is not the adapter's: it is the pool's budget (<see cref="ProbePolicy.PoolBudgets"/>), which
/// the selector spends, and whether the answer comes back at once or later (<see cref="ProbeDeliveryKind"/>).
/// </summary>
/// <typeparam name="TValue">The decision's value type.</typeparam>
public interface IProbeAdapter<TValue>
{
    /// <summary>Whether this adapter can put <paramref name="candidate"/> — its target and its method.</summary>
    bool Accepts(ProbeCandidate candidate);

    /// <summary>Puts a drawn probe.</summary>
    Task<ProbeDelivery<TValue>> PutAsync(Probe probe, CancellationToken cancellationToken = default);
}

/// <summary>The probes of a plan, put.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="Deliveries">One per drawn probe, in the plan's order. Probes that were not drawn are not here.</param>
public sealed record ProbeRun<TValue>(ProbePlan Plan, IReadOnlyList<ProbeDelivery<TValue>> Deliveries)
{
    /// <summary>
    /// The drawn probes nobody put, per pool and method. They count against the measurement as not reached: a ledger that
    /// quietly drops them divides by a smaller number than the one the probes were drawn from.
    /// </summary>
    public IReadOnlyDictionary<(string Pool, ProbeMethod Method), int> Declined => Deliveries
        .Where(d => d.Kind == ProbeDeliveryKind.Declined)
        .GroupBy(d => (d.Probe.Candidate.Pool, d.Probe.Candidate.Method))
        .ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Puts the probes a plan drew, each to the adapter that takes it.</summary>
public static class ProbeDispatch
{
    /// <summary>
    /// Puts every drawn probe of <paramref name="plan"/> to the first of <paramref name="adapters"/> that accepts it. A
    /// probe nobody accepts is <see cref="ProbeDeliveryKind.Declined"/>, said so, not skipped. Adding a tier to the ladder
    /// is a tier and, where its judge is a new kind, an adapter — the plan and the policy are the same.
    /// </summary>
    public static async Task<ProbeRun<TValue>> RunAsync<TValue>(
        ProbePlan plan, IReadOnlyList<IProbeAdapter<TValue>> adapters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(adapters);
        var deliveries = new List<ProbeDelivery<TValue>>();
        foreach (var probe in plan.Drawn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adapter = adapters.FirstOrDefault(a => a.Accepts(probe.Candidate));
            deliveries.Add(adapter is null
                ? new ProbeDelivery<TValue>(probe, ProbeDeliveryKind.Declined,
                    Reason: $"No adapter puts a {probe.Candidate.Method} probe to '{probe.Candidate.Target}'.")
                : await adapter.PutAsync(probe, cancellationToken).ConfigureAwait(false));
        }
        return new(plan, deliveries);
    }
}

/// <summary>
/// Puts a probe to a judge a router calls on the spot — a model, a memory, any tier of the ladder: the existing blind
/// <see cref="TieredRouter{TInput,TValue}.RecheckAsync"/>, or, for an anchoring probe,
/// <see cref="TieredRouter{TInput,TValue}.RecheckShownAsync"/> with the answer the consumer says to show. Every method is the
/// same call at the target's tier; what the result is <em>for</em> (a recheck record, a repeat pair, a plant, a double check)
/// is the consumer's and is told by the probe's method.
/// </summary>
/// <typeparam name="TInput">The router's input type.</typeparam>
/// <typeparam name="TValue">The decision's value type.</typeparam>
public sealed class MachineProbeAdapter<TInput, TValue> : IProbeAdapter<TValue>
{
    private readonly TieredRouter<TInput, TValue> _router;
    private readonly HashSet<string> _tiers;
    private readonly Func<ProbeCandidate, CancellationToken, ValueTask<TInput>> _inputOf;
    private readonly Func<ProbeCandidate, CancellationToken, ValueTask<string>>? _lowerAnswerOf;

    /// <param name="router">The ladder.</param>
    /// <param name="tierIds">The tiers of the ladder this adapter puts probes to — each is one judge.</param>
    /// <param name="inputOf">The input of a candidate's item: the consumer keeps it, HoneAI does not.</param>
    /// <param name="lowerAnswerOf">The answer to show a judge for an anchoring probe, rendered as the judges read it; null: anchoring probes are not accepted.</param>
    public MachineProbeAdapter(
        TieredRouter<TInput, TValue> router,
        IEnumerable<string> tierIds,
        Func<ProbeCandidate, CancellationToken, ValueTask<TInput>> inputOf,
        Func<ProbeCandidate, CancellationToken, ValueTask<string>>? lowerAnswerOf = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        ArgumentNullException.ThrowIfNull(tierIds);
        _tiers = new HashSet<string>(tierIds, StringComparer.Ordinal);
        _inputOf = inputOf ?? throw new ArgumentNullException(nameof(inputOf));
        _lowerAnswerOf = lowerAnswerOf;
    }

    /// <inheritdoc />
    public bool Accepts(ProbeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return _tiers.Contains(candidate.Target.TierId) && candidate.Target.JudgedBy is null
            && (candidate.Method != ProbeMethod.Anchoring || _lowerAnswerOf is not null);
    }

    /// <inheritdoc />
    public async Task<ProbeDelivery<TValue>> PutAsync(Probe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var candidate = probe.Candidate;
        if (!Accepts(candidate))
            return new(probe, ProbeDeliveryKind.Declined, Reason: $"This adapter does not put a {candidate.Method} probe to '{candidate.Target}'.");
        var input = await _inputOf(candidate, cancellationToken).ConfigureAwait(false);
        var result = candidate.Method == ProbeMethod.Anchoring
            ? await _router.RecheckShownAsync(
                input, candidate.DecisionId, candidate.Target.TierId, await _lowerAnswerOf!(candidate, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)
            : await _router.RecheckAsync(input, candidate.DecisionId, candidate.Target.TierId, cancellationToken).ConfigureAwait(false);
        return new(probe, ProbeDeliveryKind.Judged, result);
    }
}

/// <summary>What a pool needs to know of a probe's item that the probe does not carry.</summary>
/// <param name="MeasuredTierId">The tier whose decision is being measured (the pool keys the item under it).</param>
/// <param name="DecidedAt">When the decision was made — windows are assigned by this.</param>
/// <param name="InputRef">Where the judge finds the input: a reference, never a tier's answer.</param>
/// <param name="ExpiresAt">When the item stops being offered.</param>
public sealed record ProbeSubject(string MeasuredTierId, DateTimeOffset DecidedAt, string InputRef, DateTimeOffset ExpiresAt)
{
    /// <summary>The settling tier's confidence, carried to the ledger and never shown.</summary>
    public double? Confidence { get; init; }

    /// <summary>The selection signal's risk, carried to the ledger and never shown.</summary>
    public double? Risk { get; init; }

    /// <summary>The selection signal's weight, carried to the ledger and never shown.</summary>
    public double? Weight { get; init; }

    /// <summary>The policy snapshot the decision was made under.</summary>
    public string? SnapshotVersion { get; init; }
}

/// <summary>
/// Puts a probe to a judge that comes when it comes — a person, or an expensive model run in spare time — by setting it
/// in the recheck pool (<see cref="RecheckPool"/>) at the π the selector drew it with, so the pool's reach accounting and
/// the ledger's 1/(π·r) count it as they count any pooled item. It takes the two methods a pool item is — a recheck of a
/// settled decision and a plant. A repeat and a second opinion are made from items already answered, by the session's mix
/// (<see cref="SessionMix"/>) when the judge opens the pool, and an anchoring probe shows the judge another's answer, which a
/// pool never does: the dispatcher declines those, said so.
/// </summary>
/// <typeparam name="TValue">The decision's value type.</typeparam>
public sealed class PoolProbeAdapter<TValue> : IProbeAdapter<TValue>
{
    private readonly IRecheckPoolStore _store;
    private readonly Func<JudgeIdentity, bool> _accepts;
    private readonly Func<Probe, ProbeSubject> _subjectOf;

    /// <param name="store">The pool.</param>
    /// <param name="accepts">Which targets it serves — the rung of people (or the tier run in spare time).</param>
    /// <param name="subjectOf">What the pool needs to know of a probe's item.</param>
    public PoolProbeAdapter(IRecheckPoolStore store, Func<JudgeIdentity, bool> accepts, Func<Probe, ProbeSubject> subjectOf)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _accepts = accepts ?? throw new ArgumentNullException(nameof(accepts));
        _subjectOf = subjectOf ?? throw new ArgumentNullException(nameof(subjectOf));
    }

    /// <inheritdoc />
    public bool Accepts(ProbeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Method is ProbeMethod.Recheck or ProbeMethod.Plant && _accepts(candidate.Target);
    }

    /// <inheritdoc />
    public async Task<ProbeDelivery<TValue>> PutAsync(Probe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var candidate = probe.Candidate;
        if (!Accepts(candidate))
            return new(probe, ProbeDeliveryKind.Declined, Reason: $"This adapter does not put a {candidate.Method} probe to '{candidate.Target}'.");
        var subject = _subjectOf(probe);
        var item = new PoolItem
        {
            DecisionId = candidate.DecisionId,
            Kind = candidate.Method == ProbeMethod.Plant ? PoolItemKind.Plant : PoolItemKind.Recheck,
            TierId = subject.MeasuredTierId,
            SliceKey = candidate.SliceKey,
            DecidedAt = subject.DecidedAt,
            Probability = probe.Probability,
            Knob = $"probe/{candidate.Pool}/{candidate.Method}",
            SnapshotVersion = subject.SnapshotVersion,
            Confidence = subject.Confidence,
            Risk = subject.Risk,
            Weight = subject.Weight,
            InputRef = subject.InputRef,
            ExpiresAt = subject.ExpiresAt,
        };
        var added = await _store.AddAsync(item, cancellationToken).ConfigureAwait(false);
        return new(probe, ProbeDeliveryKind.Queued,
            Reason: added ? null : "Already in the pool; an item is pooled once per decision and tier.");
    }
}
