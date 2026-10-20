namespace HoneAI;

/// <summary>
/// The kind of judge behind a tier. Kinds fail in different ways — a statistical judge is
/// exact inside its assumptions, an ML model is confidently wrong outside its data, a language
/// model is biased in its own direction, a person tires and disagrees with themselves — which is
/// why trust is measured per tier rather than assumed from the kind.
/// </summary>
/// <remarks>
/// Persisted provenance stores these as names. The numbers of the first four are kept from the
/// enum this one replaced, and a reader of older records maps the old names
/// (<c>AutoMl</c> → <see cref="Ml"/>, <c>Frontier</c> → <see cref="Llm"/>).
/// </remarks>
public enum JudgeKind
{
    /// <summary>A closed-form or theoretical answer.</summary>
    Theory = 0,

    /// <summary>Statistical inference.</summary>
    Statistics = 1,

    /// <summary>A trained machine-learning model.</summary>
    Ml = 2,

    /// <summary>A large language model.</summary>
    Llm = 3,

    /// <summary>A fast, cheap judge: a memory of settled answers, a rule, a lightweight heuristic.</summary>
    System1 = 4,

    /// <summary>A vision-language model.</summary>
    Vlm = 5,

    /// <summary>A person.</summary>
    Human = 6,
}
