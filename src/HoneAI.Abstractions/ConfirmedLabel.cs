using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>Where a confirmed label's trust came from.</summary>
public enum LabelTrustBasis
{
    /// <summary>Measured: the confirming judge's accuracy on planted items (the teacher ledger).</summary>
    Measured = 0,

    /// <summary>
    /// Not measured yet: the default for the judge's kind. Recorded as such, so a consumer can tell a
    /// weight the ledger stands behind from one that only orders the kinds.
    /// </summary>
    Default = 1,
}

/// <summary>
/// A value a tier above — or a person — decided for a decision, ready to be learned from: the label,
/// who confirmed it, how far that judge is trusted, and the probability the decision reached it.
/// </summary>
/// <remarks>
/// Only a tier above the one that settled can confirm: a cheap tier that settles its own answer has not
/// produced a label (a judge agreeing with itself inflates every promise made on it). A label is sent to
/// each engine's sink — training rows for a model, a memory's settlement — and never stored here.
/// </remarks>
/// <typeparam name="TValue">The decision's value type.</typeparam>
public sealed record ConfirmedLabel<TValue>
{
    /// <summary>The decision the label is for.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The decision's slice, when it had one.</summary>
    public string? SliceKey { get; init; }

    /// <summary>The confirmed value.</summary>
    public required TValue Value { get; init; }

    /// <summary>The tier that confirmed it (<see cref="Tier.Id"/>) — for a person, their tier's anonymous key.</summary>
    public required string ConfirmedBy { get; init; }

    /// <summary>The confirming tier's kind.</summary>
    public required JudgeKind Kind { get; init; }

    /// <summary>The confirming tier's version.</summary>
    public string? ConfirmedByVersion { get; init; }

    /// <summary>The policy snapshot the decision was made under.</summary>
    public string? SnapshotVersion { get; init; }

    /// <summary>
    /// π — the probability the decision reached the confirming tier: 1 for a decision that went up (every
    /// such decision is judged above), the recheck draw's probability for a settled decision drawn for recheck.
    /// </summary>
    public required double Probability { get; init; }

    /// <summary>The confirming judge's estimated accuracy, in (0, 1].</summary>
    public required double Trust { get; init; }

    /// <summary>Whether <see cref="Trust"/> was measured or is the kind's default.</summary>
    public required LabelTrustBasis TrustBasis { get; init; }

    /// <summary>When the label was confirmed.</summary>
    public required DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>
    /// How much the label counts when it is learned from: <see cref="Trust"/> / <see cref="Probability"/>.
    /// The 1/π half makes the labels a sample of every decision rather than of the ones sent up or drawn;
    /// the trust half counts a judge that is wrong more often for less.
    /// </summary>
    public double Weight => Trust / Probability;

    /// <summary>
    /// Consumer references the sinks need — the engine's own id for the decision (an MLoop prediction id),
    /// an input reference. Opaque scalar strings, as in <see cref="PredictionProvenance.Annotations"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

/// <summary>What became of an upper answer offered as a label.</summary>
public enum ConfirmationOutcome
{
    /// <summary>A label: send it to the sinks.</summary>
    Confirmed = 0,

    /// <summary>
    /// Judges above disagree and one of them answered a must-catch value. Neither side wins automatically —
    /// the decision waits for a consensus judgment, and nothing is learned from it until then.
    /// </summary>
    NeedsConsensus = 1,

    /// <summary>
    /// Not a label: the answer came from the tier that settled the decision, or the decision was settled and
    /// never drawn, so no tier above saw it.
    /// </summary>
    NotALabel = 2,
}

/// <summary>The decision on an upper answer, with the label when there is one.</summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="Label">The label, when <paramref name="Outcome"/> is <see cref="ConfirmationOutcome.Confirmed"/>.</param>
/// <param name="Reason">Why, when it is not a label.</param>
public sealed record Confirmation<TValue>(ConfirmationOutcome Outcome, ConfirmedLabel<TValue>? Label = null, string? Reason = null);

/// <summary>
/// One engine's way of learning from confirmed labels — MLoop's weighted feedback, a memory's settlement,
/// a consumer's own store. Implemented per engine; HoneAI calls it and keeps nothing.
/// </summary>
public interface IConfirmedLabelSink<TValue>
{
    /// <summary>Delivers <paramref name="label"/>. A retried delivery of the same decision is the sink's to absorb.</summary>
    Task SendAsync(ConfirmedLabel<TValue> label, CancellationToken cancellationToken = default);
}
