namespace HoneAI;

/// <summary>
/// Which answers a tier may settle on its own, which answers must never be settled below the top,
/// and when two answers agree. Predicates rather than sets, so a measured value fits as well as a
/// category: "agrees" can be equality, a tolerance, or the same side of a specification limit.
/// </summary>
/// <typeparam name="TValue">The answer type.</typeparam>
public interface ISettlementPolicy<in TValue>
{
    /// <summary>
    /// True when <paramref name="tier"/> may settle <paramref name="value"/> by itself. Never true
    /// for a value <see cref="MustCatch"/> holds for.
    /// </summary>
    bool MaySettle(Tier tier, TValue value);

    /// <summary>
    /// True for an answer that must not be missed (a high risk grade, a failed part). A lower tier
    /// does not settle it; it goes up.
    /// </summary>
    bool MustCatch(TValue value);

    /// <summary>True when two answers count as the same decision.</summary>
    bool Agrees(TValue first, TValue second);
}
