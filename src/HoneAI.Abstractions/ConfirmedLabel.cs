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

    /// <summary>
    /// A consensus: two or more judges settled the value together, comparing their grounds
    /// (<see cref="ConsensusJudgment{TValue}"/>). It is the answer teachers are measured against, so its trust
    /// is set apart from any single judge's.
    /// </summary>
    Consensus = 2,
}

/// <summary>
/// What a confirmed label is for: learning, or measuring the tier that learns. A label measuring a model must not be
/// one the model learned from, so the two are kept apart by the decision, not by the consumer's hand.
/// </summary>
public enum LabelShare
{
    /// <summary>For the learning engines — training rows for a model, a memory's settlement.</summary>
    Training = 0,

    /// <summary>
    /// Held out: for measuring the learning tier offline (a challenger against the model in production), never sent to
    /// an engine that learns. Sinks that train refuse it.
    /// </summary>
    Evaluation = 1,
}

/// <summary>How a decision reached the judge whose answer became a label.</summary>
public enum LabelOrigin
{
    /// <summary>
    /// The decision went up, or was drawn for recheck with a known probability π — the label is a sample of every
    /// such decision, and its weight divides by π.
    /// </summary>
    Drawn = 0,

    /// <summary>
    /// A person corrected a decision nothing drew: there is no π. People correct what they see wrong, so these labels
    /// are a sample of the errors, not of the decisions — they teach (their weight is capped, never divided by a
    /// probability they do not have) and never measure (<see cref="LabelShare.Evaluation"/> refuses them), and they are
    /// counted apart from the person's measured trust.
    /// </summary>
    Voluntary = 1,

    /// <summary>
    /// Nobody judged this decision: a person's answer to a representative of its cluster was spread to it. The value is an
    /// estimate of what a person would say, not an observation — so a propagated label teaches (at its representative's trust
    /// times the cluster's measured purity, capped) and never measures (<see cref="LabelShare.Evaluation"/> refuses it), and it
    /// is never counted in anyone's measured trust. It has no π: it stands for itself, not for the decisions nobody looked at.
    /// </summary>
    Propagated = 2,
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

    /// <summary>
    /// The tier that confirmed it (<see cref="Tier.Id"/>). For a consensus, its <see cref="Participants"/> joined
    /// by <c>+</c> in the cost order.
    /// </summary>
    public required string ConfirmedBy { get; init; }

    /// <summary>
    /// Which judge within the confirming tier answered (<see cref="JudgeIdentity.JudgedBy"/>) — the person's
    /// pseudonymous key on a rung of people. Null for a tier that is one judge, and for a consensus.
    /// </summary>
    public string? JudgedBy { get; init; }

    /// <summary>The judges that reached a consensus, in the cost order; null for a label one judge confirmed.</summary>
    public IReadOnlyList<JudgeIdentity>? Participants { get; init; }

    /// <summary>The confirming tier's kind — for a consensus, the kind of the participant highest in the cost order.</summary>
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
    /// Whether the label is for learning or held out for measuring — fixed by the decision, so every label a decision
    /// ever gets falls in the same share. <see cref="LabelShare.Training"/> when no split was asked for.
    /// </summary>
    public LabelShare Share { get; init; } = LabelShare.Training;

    /// <summary>
    /// q — the probability a decision's labels fall in <see cref="Share"/>, in (0, 1]: 1 when nothing is held out.
    /// </summary>
    public double ShareProbability { get; init; } = 1.0;

    /// <summary>
    /// The key the split dealt this label's decision by, when the unit was bigger than the decision (decisions with one
    /// normalized input); null when the decision itself was the unit. It counts the independent units of a share
    /// (<see cref="Unit"/>).
    /// </summary>
    public string? SplitKey { get; init; }

    /// <summary>The independent unit of the split this label belongs to: <see cref="SplitKey"/>, else the decision.</summary>
    public string Unit => SplitKey ?? DecisionId;

    /// <summary>
    /// s — the probability the label was kept when the training set was selected (<c>TrainingSetSelector</c>), in (0, 1]:
    /// 1 unless the selection was asked to stand for the labels it left out (<c>TrainingSetPolicy.Compensate</c>).
    /// </summary>
    public double SelectionProbability { get; init; } = 1.0;

    /// <summary>
    /// How much the label counts: <see cref="Trust"/> / (<see cref="Probability"/> · <see cref="ShareProbability"/> ·
    /// <see cref="SelectionProbability"/>). The 1/π half makes the labels a sample of every decision rather than of the
    /// ones sent up or drawn, the 1/q half keeps each share a sample of them too, and the 1/s half — when the selection
    /// asked for it — makes the labels kept stand for the ones left out; the trust half counts a judge that is wrong
    /// more often for less.
    /// </summary>
    public double Weight => Math.Min(Trust * Purity / (Probability * ShareProbability * SelectionProbability), WeightCap ?? double.PositiveInfinity);

    /// <summary>
    /// How the decision reached the judge — <see cref="LabelOrigin.Drawn"/> unless a person corrected a decision nothing
    /// drew (<see cref="LabelOrigin.Voluntary"/>, <c>LabelConfirmation.Volunteer</c>).
    /// </summary>
    public LabelOrigin Origin { get; init; } = LabelOrigin.Drawn;

    /// <summary>
    /// The most <see cref="Weight"/> may be, when the label has no π to divide by — a voluntary label stands for itself,
    /// not for decisions it was not drawn from. Null: no cap.
    /// </summary>
    public double? WeightCap { get; init; }

    /// <summary>
    /// For a <see cref="LabelOrigin.Propagated"/> label, the share of the cluster's decisions the representative's value was
    /// measured to be right for, in (0, 1] — the weight is multiplied by it. 1 for every other label.
    /// </summary>
    public double Purity { get; init; } = 1.0;

    /// <summary>For a <see cref="LabelOrigin.Propagated"/> label, the decision whose answer was spread; null otherwise.</summary>
    public string? PropagatedFrom { get; init; }

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

    /// <summary>
    /// A label the consumer's admission rules keep out: the confirming judge is not qualified, or trusted under the
    /// minimum. Nothing is learned from it.
    /// </summary>
    NotAdmitted = 3,
}

/// <summary>
/// The value two or more judges settled together for a decision whose judges above disagreed on a must-catch
/// value (<see cref="ConfirmationOutcome.NeedsConsensus"/>) — by comparing their grounds, not by rank: neither
/// original answer wins automatically, and the value may be neither. Reaching it (who sits down, how the grounds
/// are shown) is the consumer's; HoneAI turns it into a label and keeps nothing.
/// </summary>
/// <typeparam name="TValue">The decision's value type.</typeparam>
public sealed record ConsensusJudgment<TValue>
{
    /// <summary>The decision it settles.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The value settled on.</summary>
    public required TValue Value { get; init; }

    /// <summary>
    /// The judges that took part — two or more, distinct. Two people on one rung are two judges
    /// (<see cref="JudgeIdentity.JudgedBy"/>), as are judges of two tiers.
    /// </summary>
    public required IReadOnlyList<JudgeIdentity> Participants { get; init; }

    /// <summary>When it was reached.</summary>
    public required DateTimeOffset ReachedAt { get; init; }

    /// <summary>What the grounds compared were, for the audit — a reference, not a transcript.</summary>
    public string? Grounds { get; init; }
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
