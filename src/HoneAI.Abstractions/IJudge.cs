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

/// <summary>What a judge is told about the decision besides the input.</summary>
public sealed record JudgeContext
{
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

    /// <summary>An answer.</summary>
    public static JudgeVerdict<TValue> Answer(TValue value, double? confidence = null, string? rationale = null)
        => new() { Answered = true, Value = value, Confidence = confidence, Rationale = rationale };

    /// <summary>An abstention, with the reason.</summary>
    public static JudgeVerdict<TValue> Abstain(string reason)
        => new() { Answered = false, Rationale = reason };
}
