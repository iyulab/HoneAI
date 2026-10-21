using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// A judge: given an input, answers or abstains. It is given the input only — never what another
/// tier answered — so a tier above can check a tier below without being anchored by it.
/// </summary>
/// <typeparam name="TInput">What is judged.</typeparam>
/// <typeparam name="TValue">The answer — a category, a grade, a measured value.</typeparam>
public interface IJudge<in TInput, TValue>
{
    /// <summary>The tier this judge occupies.</summary>
    Tier Tier { get; }

    /// <summary>
    /// Answer or abstain. A judge that cannot decide abstains with a reason rather than guessing;
    /// a judge that throws or times out is recorded as having abstained.
    /// </summary>
    ValueTask<JudgeVerdict<TValue>> JudgeAsync(TInput input, JudgeContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Who answered: a tier, and — when the tier is many judges answering in turn, as people on one rung are —
/// which of them (<see cref="JudgedBy"/>). Trust is measured per identity: two people on the same rung tire,
/// disagree and are right at different rates, and a label one of them confirmed is weighted by theirs.
/// </summary>
/// <param name="TierId">The tier (<see cref="Tier.Id"/>).</param>
/// <param name="JudgedBy">
/// Which judge within the tier — a pseudonymous key for a person (<see cref="ReviewDecision.ReviewerKey"/>),
/// never a name. Null for a tier that is one judge.
/// </param>
public sealed record JudgeIdentity(string TierId, string? JudgedBy = null)
{
    /// <summary><c>tier</c>, or <c>tier/judge</c> for a judge within a tier.</summary>
    public override string ToString() => JudgedBy is null ? TierId : $"{TierId}/{JudgedBy}";
}

/// <summary>What a judge is told about the decision besides the input.</summary>
public sealed record JudgeContext
{
    /// <summary>
    /// The hint key under which a shown recheck carries the lower tier's answer — set only by the
    /// paired measurement of what showing it does, never on a recheck the ledger counts.
    /// </summary>
    public const string LowerAnswerHint = "lower-answer";

    /// <summary>Identifier of the decision, stable across retries.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The slice the input falls in.</summary>
    public required string SliceKey { get; init; }

    /// <summary>
    /// True when this judgment rechecks a decision a lower tier already settled. A recheck carries
    /// no hints and no lower answer.
    /// </summary>
    public bool IsRecheck { get; init; }

    /// <summary>
    /// The share of its least confident answers this tier should abstain on, when the policy sets
    /// one — confidence is not a licence to settle, but its ranking says what to send up.
    /// </summary>
    public double? AbstainQuantile { get; init; }

    /// <summary>
    /// Hints passed up the escalation path, for a tier that <see cref="Tier.AcceptsHints"/>. Opaque
    /// strings, as in provenance annotations. Never set on a recheck.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Hints { get; init; }
}

/// <summary>A judge's answer, or its abstention.</summary>
/// <typeparam name="TValue">The answer type.</typeparam>
public sealed record JudgeVerdict<TValue>
{
    private JudgeVerdict()
    {
    }

    /// <summary>True when the judge answered; false when it abstained.</summary>
    public bool Answered { get; private init; }

    /// <summary>The answer; default when the judge abstained.</summary>
    public TValue? Value { get; private init; }

    /// <summary>
    /// The judge's confidence in [0, 1], when it reports one and it means a probability. A person
    /// or a memory may have none.
    /// </summary>
    public double? Confidence { get; private init; }

    /// <summary>Why — the grounds of the answer, or the reason for abstaining.</summary>
    public string? Rationale { get; private init; }

    /// <summary>
    /// Which judge within the tier answered (<see cref="JudgeIdentity.JudgedBy"/>) — the person, for a rung of
    /// people. Null for a tier that is one judge.
    /// </summary>
    public string? JudgedBy { get; private init; }

    /// <summary>
    /// What this judge can pass up to a tier that accepts hints when the decision goes up — what it
    /// narrowed the answer to, for instance. Never its answer.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Hints { get; private init; }

    /// <summary>An answer.</summary>
    public static JudgeVerdict<TValue> Answer(
        TValue value, double? confidence = null, string? rationale = null, IReadOnlyDictionary<string, string>? hints = null,
        string? judgedBy = null)
        => new() { Answered = true, Value = value, Confidence = confidence, Rationale = rationale, Hints = hints, JudgedBy = judgedBy };

    /// <summary>An abstention, with the reason.</summary>
    public static JudgeVerdict<TValue> Abstain(
        string reason, IReadOnlyDictionary<string, string>? hints = null, string? judgedBy = null)
        => new() { Answered = false, Rationale = reason, Hints = hints, JudgedBy = judgedBy };
}
