using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>How a blind recheck of a settled decision came out.</summary>
public enum RecheckOutcome
{
    /// <summary>The tier above answered, and its answer agrees with the settled one (by the settlement policy's predicate).</summary>
    Agreed = 0,

    /// <summary>The tier above answered differently.</summary>
    Disagreed = 1,

    /// <summary>
    /// The tier above gave no answer — it abstained, was cut off, or timed out. Not random: hard
    /// inputs fail more often, so the ledger counts these at both ends of an interval rather than
    /// dropping them.
    /// </summary>
    UpperFailed = 2,
}

/// <summary>
/// One settled decision that was drawn for recheck, and what the tier above said about it.
/// </summary>
/// <remarks>
/// Every recheck is of a decision a lower tier <i>settled</i> — a decision that went up was judged
/// above anyway and needs no recheck. So a recheck whose upper answer is a must-catch value is a
/// miss: the lower tier settled something it should have sent up.
/// </remarks>
public sealed record RecheckRecord
{
    /// <summary>The decision's id — with <see cref="TierId"/>, the key a recheck is recorded under once.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The tier that settled the decision (the one being measured).</summary>
    public required string TierId { get; init; }

    /// <summary>The input's slice.</summary>
    public required string SliceKey { get; init; }

    /// <summary>
    /// When the decision was made. Windows are assigned by this, not by when the recheck came back,
    /// so a late result does not move a window it does not belong to.
    /// </summary>
    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>The probability π the decision was drawn with (from its provenance's sample draw).</summary>
    public required double Probability { get; init; }

    /// <summary>The knob that set π.</summary>
    public string? Knob { get; init; }

    /// <summary>Every knob's rate when the decision was drawn (<see cref="SampleDraw.KnobRates"/>).</summary>
    public IReadOnlyDictionary<string, double>? KnobRates { get; init; }

    /// <summary>The policy snapshot the decision was made under.</summary>
    public string? SnapshotVersion { get; init; }

    /// <summary>How the recheck came out.</summary>
    public required RecheckOutcome Outcome { get; init; }

    /// <summary>The tier above answered with a must-catch value. Meaningless when <see cref="Outcome"/> is <see cref="RecheckOutcome.UpperFailed"/>.</summary>
    public bool UpperMustCatch { get; init; }

    /// <summary>The tier that rechecked (the teacher).</summary>
    public string? CheckedBy { get; init; }

    /// <summary>The checker did not see the settled answer. A recheck is blind by default.</summary>
    public bool Blind { get; init; } = true;
}

/// <summary>
/// Where recheck results are kept and read back. Implemented by the consumer for its operational
/// store; HoneAI.Core has in-memory and JSONL reference implementations.
/// </summary>
public interface IRecheckStore
{
    /// <summary>
    /// Records <paramref name="record"/> unless a record for the same decision and tier exists;
    /// returns whether it was recorded. A retried or duplicated delivery does not count twice.
    /// </summary>
    ValueTask<bool> AppendAsync(RecheckRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// The records of <paramref name="tierId"/> in <paramref name="sliceKey"/> whose decision time
    /// falls in [<paramref name="from"/>, <paramref name="to"/>).
    /// </summary>
    IAsyncEnumerable<RecheckRecord> ReadAsync(
        string tierId, string sliceKey, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One cell of the trust ledger: a tier in a slice over a window of decision time.
/// </summary>
public sealed record LedgerCell
{
    public required string TierId { get; init; }
    public required string SliceKey { get; init; }
    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>Rechecks in the window, failed ones included.</summary>
    public required int Rechecks { get; init; }

    /// <summary>Rechecks the tier above did not answer.</summary>
    public required int UpperFailures { get; init; }

    /// <summary>Share of rechecks the tier above did not answer (unweighted).</summary>
    public double UpperFailureRate => Rechecks == 0 ? 0 : (double)UpperFailures / Rechecks;

    /// <summary>
    /// Kish effective sample size of the answered rechecks under their 1/π weights — what the
    /// interval's width rests on, and a condition for a tier to gain ground.
    /// </summary>
    public required double EffectiveSampleSize { get; init; }

    /// <summary>
    /// Agreement over the answered rechecks, each weighted by 1/π (Horvitz–Thompson ratio).
    /// Null when nothing was answered.
    /// </summary>
    public double? Agreement { get; init; }

    /// <summary>Lower end of the agreement interval, with every failed recheck counted as a disagreement.</summary>
    public double? AgreementLower { get; init; }

    /// <summary>Upper end of the agreement interval, with every failed recheck counted as an agreement.</summary>
    public double? AgreementUpper { get; init; }

    /// <summary>Must-catch decisions the tier settled, estimated from the rechecks (Σ 1/π over the misses seen).</summary>
    public required double MissesEstimated { get; init; }

    /// <summary>Must-catch decisions the tier sent up and the tier above confirmed — fully observed, not sampled.</summary>
    public required int Caught { get; init; }

    /// <summary>Conditional miss rate: misses ÷ (misses + caught). Null with neither.</summary>
    public double? MissRate { get; init; }

    /// <summary>
    /// Upper bound on the conditional miss rate at the ledger's confidence, counting every failed
    /// recheck as a miss (the worst it could hide). Null when there is no caught decision and no
    /// miss to bound against.
    /// </summary>
    public double? MissRateUpper { get; init; }

    /// <summary>The confidence the interval and the bound are stated at.</summary>
    public required double Confidence { get; init; }
}
