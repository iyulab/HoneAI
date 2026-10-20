using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI;

/// <summary>Ready-made <see cref="ISettlementPolicy{TValue}"/> shapes.</summary>
public static class SettlementPolicy
{
    /// <summary>
    /// Categories: every tier may settle the <paramref name="settleable"/> answers, none may settle
    /// the <paramref name="mustCatch"/> ones, and two answers agree when they are equal.
    /// </summary>
    public static ISettlementPolicy<TValue> Categorical<TValue>(
        IEnumerable<TValue> settleable, IEnumerable<TValue> mustCatch, IEqualityComparer<TValue>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(settleable);
        ArgumentNullException.ThrowIfNull(mustCatch);
        comparer ??= EqualityComparer<TValue>.Default;
        var settle = settleable.ToHashSet(comparer);
        var catchSet = mustCatch.ToHashSet(comparer);
        return Create<TValue>((_, v) => settle.Contains(v), catchSet.Contains, comparer.Equals);
    }

    /// <summary>
    /// Measured values: two answers agree when they differ by at most <paramref name="tolerance"/>.
    /// Every value a tier may settle unless <paramref name="mustCatch"/> holds for it.
    /// </summary>
    public static ISettlementPolicy<double> Tolerance(double tolerance, Func<double, bool>? mustCatch = null)
    {
        if (!(tolerance >= 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be zero or positive.");
        var mc = mustCatch ?? (_ => false);
        return Create<double>((_, _) => true, mc, (a, b) => Math.Abs(a - b) <= tolerance);
    }

    /// <summary>
    /// Measured values judged against a specification limit: two answers agree when they fall on
    /// the same side of <paramref name="limit"/> — what matters is the pass/fail they imply, not
    /// the digits.
    /// </summary>
    public static ISettlementPolicy<double> SameSideOf(double limit, Func<double, bool>? mustCatch = null)
    {
        var mc = mustCatch ?? (_ => false);
        return Create<double>((_, _) => true, mc, (a, b) => a >= limit == b >= limit);
    }

    /// <summary>A policy from three predicates. A value <paramref name="mustCatch"/> holds for is never settleable.</summary>
    public static ISettlementPolicy<TValue> Create<TValue>(
        Func<Tier, TValue, bool> maySettle, Func<TValue, bool> mustCatch, Func<TValue, TValue, bool> agrees)
        => new DelegatePolicy<TValue>(
            maySettle ?? throw new ArgumentNullException(nameof(maySettle)),
            mustCatch ?? throw new ArgumentNullException(nameof(mustCatch)),
            agrees ?? throw new ArgumentNullException(nameof(agrees)));

    private sealed class DelegatePolicy<TValue>(
        Func<Tier, TValue, bool> maySettle, Func<TValue, bool> mustCatch, Func<TValue, TValue, bool> agrees)
        : ISettlementPolicy<TValue>
    {
        public bool MaySettle(Tier tier, TValue value) => !mustCatch(value) && maySettle(tier, value);
        public bool MustCatch(TValue value) => mustCatch(value);
        public bool Agrees(TValue first, TValue second) => agrees(first, second);
    }
}
