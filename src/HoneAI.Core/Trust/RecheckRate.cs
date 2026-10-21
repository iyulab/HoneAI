using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>
/// What a consumer promises about one tier, and how fast a breach must show. Only people set these:
/// the controller derives recheck probabilities from them and never moves them.
/// </summary>
public sealed record TrustTargets
{
    /// <summary>The agreement with the tier above the tier is held to (p0).</summary>
    public required double Agreement { get; init; }

    /// <summary>A drop in agreement the window must be able to see (δ).</summary>
    public required double DetectableDrop { get; init; }

    /// <summary>
    /// The largest conditional must-catch miss rate tolerated (m) — of the must-catch decisions, the
    /// share the tier settled instead of sending up. Misses are not prevented in real time; they are
    /// found within <see cref="Deadline"/>, and up to this rate is accepted in the meantime.
    /// </summary>
    public required double MissLimit { get; init; }

    /// <summary>How long a breach may go unseen (D) — the window a release is judged on.</summary>
    public required TimeSpan Deadline { get; init; }

    /// <summary>Significance of the bounds (α).</summary>
    public double Alpha { get; init; } = 0.05;

    /// <summary>Probability of seeing a drop of <see cref="DetectableDrop"/> when there is one (1 − β).</summary>
    public double Power { get; init; } = 0.8;

    /// <summary>The smallest recheck probability any settled decision is drawn at. Above zero, always.</summary>
    public double RateFloor { get; init; } = 0.01;

    /// <summary>The largest recheck probability — at it, the tier saves nothing.</summary>
    public double RateCeiling { get; init; } = 1.0;
}

/// <summary>What a slice is expected to see in one window of <see cref="TrustTargets.Deadline"/>.</summary>
/// <param name="Decisions">Decisions the tier settles in the slice.</param>
/// <param name="Caught">Must-catch decisions the tier sends up and the tier above confirms (C_D).</param>
public sealed record SliceTraffic(double Decisions, double Caught);

/// <summary>Which term of <see cref="RecheckRate.Solve"/> set a recheck probability.</summary>
public static class RateTerm
{
    /// <summary>The must-catch bound: enough misses must be drawn to bound the miss rate by m.</summary>
    public const string Miss = "miss";

    /// <summary>The agreement test: enough rechecks to see a drop of δ at the window's end.</summary>
    public const string Agreement = "agreement";

    /// <summary>Neither term needed more than the floor.</summary>
    public const string Floor = "floor";

    /// <summary>The budget could not fund the rate either term needed (<see cref="RecheckBudget"/>).</summary>
    public const string Budget = "budget";
}

/// <summary>A recheck probability and why it is what it is.</summary>
/// <param name="Rate">The probability, within [floor, ceiling].</param>
/// <param name="SetBy">The term that set it (<see cref="RateTerm"/>).</param>
/// <param name="MissTerm">π the miss bound needs, before clipping; infinite when no must-catch decision is expected.</param>
/// <param name="AgreementTerm">π the agreement test needs, before clipping; infinite when no decision is expected.</param>
/// <param name="Capped">
/// The term needed more than the ceiling. A capped slice cannot show its promise within the deadline
/// and stays with the tier above — by design for small slices where risk is rare.
/// </param>
public sealed record RecheckRateChoice(double Rate, string SetBy, double MissTerm, double AgreementTerm, bool Capped);

/// <summary>
/// Derives the recheck probability a stable tier needs in a slice from what the consumer promised.
/// </summary>
/// <remarks>
/// <para>
/// <b>Misses.</b> A miss is seen only when its decision is drawn, so with M misses in a window and
/// none drawn, M ≤ ln(1/α)/π at confidence 1 − α. Bounding the conditional rate M / (M + C) by m
/// then needs π_miss = ln(1/α) · (1 − m) / (m · C), C being the must-catch decisions caught in the
/// window. The rarer risk is in a slice, the larger π — at the ceiling, the slice stays up.
/// </para>
/// <para>
/// <b>Agreement.</b> Release is a fixed-sample test at the window's end, so it needs
/// n = (z_{1−α} + z_{power})² · p0(1 − p0) / δ² rechecks (one-sided), and π_agreement = n / decisions.
/// Tightening uses an anytime-valid bound that is wider at the same α — it fires early on a clear
/// drop, while release is judged exactly at the boundary.
/// </para>
/// <para>π = clip(max(π_miss, π_agreement), floor, ceiling).</para>
/// </remarks>
public static class RecheckRate
{
    /// <summary>The recheck probability <paramref name="traffic"/> needs under <paramref name="targets"/>.</summary>
    public static RecheckRateChoice Solve(TrustTargets targets, SliceTraffic traffic)
    {
        Validate(targets);
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Decisions < 0 || traffic.Caught < 0)
            throw new ArgumentOutOfRangeException(nameof(traffic), traffic, "Expected counts cannot be negative.");

        var miss = traffic.Caught > 0
            ? Math.Log(1 / targets.Alpha) * (1 - targets.MissLimit) / (targets.MissLimit * traffic.Caught)
            : double.PositiveInfinity;
        var agreement = traffic.Decisions > 0
            ? RechecksForAgreement(targets) / traffic.Decisions
            : double.PositiveInfinity;

        var needed = Math.Max(miss, agreement);
        var setBy = needed <= targets.RateFloor ? RateTerm.Floor : miss >= agreement ? RateTerm.Miss : RateTerm.Agreement;
        var rate = Math.Clamp(needed, targets.RateFloor, targets.RateCeiling);
        return new RecheckRateChoice(rate, setBy, miss, agreement, needed > targets.RateCeiling);
    }

    /// <summary>
    /// Rechecks a window needs to see a drop of <see cref="TrustTargets.DetectableDrop"/> from
    /// <see cref="TrustTargets.Agreement"/>: (z_{1−α} + z_{power})² · p0(1 − p0) / δ².
    /// </summary>
    public static double RechecksForAgreement(TrustTargets targets)
    {
        Validate(targets);
        var z = TrustLedger.NormalQuantile(1 - targets.Alpha) + TrustLedger.NormalQuantile(targets.Power);
        return z * z * targets.Agreement * (1 - targets.Agreement) / (targets.DetectableDrop * targets.DetectableDrop);
    }

    internal static void Validate(TrustTargets targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        static void Open(double value, string name)
        {
            if (!(value > 0 && value < 1))
                throw new ArgumentOutOfRangeException(name, value, $"{name} lies strictly between 0 and 1.");
        }
        Open(targets.Agreement, nameof(TrustTargets.Agreement));
        Open(targets.DetectableDrop, nameof(TrustTargets.DetectableDrop));
        Open(targets.MissLimit, nameof(TrustTargets.MissLimit));
        Open(targets.Alpha, nameof(TrustTargets.Alpha));
        Open(targets.Power, nameof(TrustTargets.Power));
        if (targets.Deadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(targets), targets.Deadline, "The deadline must be positive.");
        if (!(targets.RateFloor > 0 && targets.RateFloor <= targets.RateCeiling && targets.RateCeiling <= 1))
            throw new ArgumentOutOfRangeException(nameof(targets), targets.RateFloor,
                "Recheck probabilities need 0 < floor ≤ ceiling ≤ 1: a settled decision is never beyond recheck.");
    }
}

/// <summary>One slice asking for rechecks out of a pool.</summary>
/// <param name="SliceKey">The slice.</param>
/// <param name="Decisions">Decisions the tier is expected to settle in the slice per window.</param>
/// <param name="Need">The rate the slice needs (<see cref="RecheckRate.Solve"/>).</param>
/// <param name="MissExposure">
/// Upper bound on the must-catch decisions the slice may be missing per window — its miss-rate upper
/// bound times its must-catch volume. <c>null</c> for a slice not yet measured, which ranks first:
/// the bound of what is unknown is large, so a new slice is funded before a known one.
/// </param>
public sealed record BudgetRequest(string SliceKey, double Decisions, RecheckRateChoice Need, double? MissExposure);

/// <summary>A slice's share of the pool.</summary>
/// <param name="SliceKey">The slice.</param>
/// <param name="Rate">The probability its settled decisions are drawn at.</param>
/// <param name="Rechecks">Expected rechecks per window: rate × decisions.</param>
/// <param name="Funded">It got the rate it needs; a slice that did not cannot meet its release conditions and stays where it is.</param>
/// <param name="SetBy">The term that set the rate, or <see cref="RateTerm.Budget"/> when the pool ran short.</param>
public sealed record BudgetGrant(string SliceKey, double Rate, double Rechecks, bool Funded, string SetBy);

/// <summary>How one pool's rechecks were shared, and whether the floors alone overran it.</summary>
public sealed record BudgetAllocation(IReadOnlyList<BudgetGrant> Grants, double Spent, double Budget)
{
    /// <summary>The floors every slice keeps cost more than the pool holds — the floors were kept.</summary>
    public bool OverBudget => Spent > Budget + 1e-9;
}

/// <summary>
/// Shares one pool of tier-above capacity (a tenant's share of throughput, a day's cost, a person's
/// weekly hours — in rechecks per window) among the slices that ask for it.
/// </summary>
/// <remarks>
/// Every slice keeps its floor first, whatever the pool holds — a settled decision is never beyond
/// recheck, and that is the minimum exploration that keeps an unmeasured slice from staying
/// unmeasured. The rest goes to the slices whose possible misses are largest, a new slice first; a
/// slice is funded in full before the next is considered, and the last one funded may get part of
/// its need. A shortfall leaves slices unfunded, which keeps them with the tier above: the pool
/// leans toward quality, never toward settling unverified. Planting budget is a separate pool.
/// </remarks>
public static class RecheckBudget
{
    /// <summary>Shares <paramref name="budget"/> rechecks per window among <paramref name="requests"/>.</summary>
    public static BudgetAllocation Allocate(double budget, IReadOnlyList<BudgetRequest> requests, double rateFloor)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (budget < 0 || double.IsNaN(budget))
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "The budget cannot be negative.");
        if (!(rateFloor > 0 && rateFloor <= 1))
            throw new ArgumentOutOfRangeException(nameof(rateFloor), rateFloor, "The floor lies in (0, 1].");
        if (requests.GroupBy(r => r.SliceKey, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            throw new ArgumentException($"Slice '{repeated.Key}' asks twice.", nameof(requests));

        var rates = requests.ToDictionary(r => r.SliceKey, r => Math.Min(rateFloor, r.Need.Rate), StringComparer.Ordinal);
        var spent = requests.Sum(r => rates[r.SliceKey] * r.Decisions);

        var left = budget - spent;
        foreach (var r in requests.OrderByDescending(r => r.MissExposure ?? double.PositiveInfinity).ThenBy(r => r.SliceKey, StringComparer.Ordinal))
        {
            if (left <= 0)
                break;
            if (r.Decisions <= 0)
                continue;
            var extra = (r.Need.Rate - rates[r.SliceKey]) * r.Decisions;
            if (extra <= 0)
                continue;
            var granted = Math.Min(extra, left);
            rates[r.SliceKey] += granted / r.Decisions;
            left -= granted;
            spent += granted;
        }

        var grants = requests.Select(r =>
        {
            var rate = rates[r.SliceKey];
            var funded = rate >= r.Need.Rate - 1e-12;
            return new BudgetGrant(r.SliceKey, rate, rate * r.Decisions, funded, funded ? r.Need.SetBy : RateTerm.Budget);
        }).ToList();
        return new BudgetAllocation(grants, spent, budget);
    }
}
