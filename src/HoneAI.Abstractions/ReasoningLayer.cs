namespace HoneAI;

/// <summary>
/// The reasoning layer that produced (or escalated) an answer, ordered cheap→costly.
/// Routing climbs only as far as confidence requires (§0 "싼·확실한 레이어 우선").
/// </summary>
public enum ReasoningLayer
{
    /// <summary>L0 — theory / closed-form answer (formulab). Cheapest, most certain.</summary>
    Theory = 0,

    /// <summary>L1 — statistical inference (u-insight / u-analytics).</summary>
    Statistics = 1,

    /// <summary>L2 — verified AutoML prediction (MLoop).</summary>
    AutoMl = 2,

    /// <summary>L3 — frontier LLM, reached via a provider-neutral IChatClient.</summary>
    Frontier = 3,
}
