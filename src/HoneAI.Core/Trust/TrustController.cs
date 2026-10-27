using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>
/// A person's hold on one tier in one slice: the controller stops <em>releasing</em> it — no move toward active or
/// stable, no step along the windows that lead there, no lowering of the rate it is rechecked at — until the hold is
/// lifted. Tightening is never held: a frozen slice that gets worse is still demoted and rechecked more, or the lever
/// meant to stop an automatic change would stop the safety net with it. The ledger keeps measuring either way.
/// </summary>
/// <param name="By">Who froze it.</param>
/// <param name="At">When.</param>
/// <param name="Reason">Why — what a person coming to lift the hold reads first.</param>
/// <param name="MinimumRate">
/// The rate the slice is rechecked at no lower than, when it is stable (its pool grant may fall below what the person
/// saw when freezing it). Null: the grant as it comes. The extra rechecks are counted in the pool's spending.
/// </param>
public sealed record SliceFreeze(string By, DateTimeOffset At, string Reason, double? MinimumRate = null)
{
    /// <summary>The term that sets a stable slice's rate when this hold raised it (<see cref="BudgetGrant.SetBy"/>).</summary>
    public const string RateTerm = "freeze";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(By);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
        if (MinimumRate is { } r && !(r > 0 && r <= 1))
            throw new ArgumentOutOfRangeException(nameof(MinimumRate), r, "A rate lies in (0, 1].");
    }

    internal string Describe() => $"frozen by {By} at {At:yyyy-MM-dd HH:mm}Z: {Reason}";
}

/// <summary>One tier in one slice at a window's end: where it stands and what the window showed.</summary>
/// <param name="TierId">The tier.</param>
/// <param name="SliceKey">The slice.</param>
/// <param name="Pool">The pool of tier-above capacity its rechecks come from (a tenant's share, a person's hours).</param>
/// <param name="Standing">Its committed standing.</param>
/// <param name="Window">The ledger cell over the closed window.</param>
/// <param name="TeacherQualified">The tier above is qualified to teach it.</param>
/// <param name="Signals">Its live signals (version, drift).</param>
/// <param name="Traffic">What the slice is expected to see in the next window.</param>
public sealed record SliceWindow(
    string TierId, string SliceKey, string Pool, SliceStanding Standing, LedgerCell Window,
    bool TeacherQualified, LiveSignals Signals, SliceTraffic Traffic)
{
    /// <summary>Rates of knobs other than the promise knob.</summary>
    public IReadOnlyDictionary<string, double>? OtherKnobRates { get; init; }

    /// <summary>A person's hold on the slice (<see cref="SliceFreeze"/>); null when it is not frozen.</summary>
    public SliceFreeze? Freeze { get; init; }

    /// <summary>How knobs tilt toward decisions worth checking.</summary>
    public IReadOnlyDictionary<string, KnobSelection>? Selection { get; init; }

    /// <summary>
    /// The abstain quantile wanted for the tier. Issued only where the window shows its confidence ranks
    /// its mistakes (<see cref="ConfidenceAbstain"/>) and it is <see cref="Calibrated"/>.
    /// </summary>
    public double? AbstainQuantile { get; init; }

    /// <summary>
    /// The tier's confidence passed its calibration check (for an ML tier, MLoop's <c>ece</c>). True
    /// for a tier whose confidence has no calibration to check.
    /// </summary>
    public bool Calibrated { get; init; } = true;
}

/// <summary>Everything the controller applies. People set all of it.</summary>
/// <param name="Lifecycle">How standings move.</param>
/// <param name="Issue">How snapshots are issued.</param>
/// <param name="PoolBudgets">Rechecks per window each pool holds.</param>
public sealed record ControllerPolicy(LifecyclePolicy Lifecycle, IssuePolicy Issue, IReadOnlyDictionary<string, double> PoolBudgets);

/// <summary>What the controller decided for one tier in one slice.</summary>
/// <param name="TierId">The tier.</param>
/// <param name="SliceKey">The slice.</param>
/// <param name="Step">The lifecycle's judgement. A release that awaits approval is not in the snapshot; one a freeze held is a stay that says so.</param>
/// <param name="Committed">The standing the snapshot was issued with.</param>
/// <param name="Need">The recheck probability the promise needs here.</param>
/// <param name="Grant">The pool's grant, for a stable tier.</param>
public sealed record SliceOutcome(string TierId, string SliceKey, StandingChange Step, SliceStanding Committed, RecheckRateChoice Need, BudgetGrant? Grant)
{
    /// <summary>The hold the slice was under (<see cref="SliceFreeze"/>); null when it was not frozen.</summary>
    public SliceFreeze? Freeze { get; init; }

    /// <summary>The freeze held a release or a rate cut this window — <see cref="Step"/> says which.</summary>
    public bool Held { get; init; }
}

/// <summary>A window's close: the snapshot to send to sites, what each slice got and why, and each pool's sharing.</summary>
public sealed record ControllerStep(PolicySnapshot Snapshot, IReadOnlyList<SliceOutcome> Slices, IReadOnlyDictionary<string, BudgetAllocation> Pools)
{
    /// <summary>Releases a person must approve before they take effect.</summary>
    public IEnumerable<SliceOutcome> AwaitingApproval => Slices.Where(s => s.Step.AwaitsApproval);

    /// <summary>Slices a person has frozen, whether or not the freeze held anything this window.</summary>
    public IEnumerable<SliceOutcome> Frozen => Slices.Where(s => s.Freeze is not null);
}

/// <summary>
/// Closes a window: judges every tier in every slice (<see cref="TrustLifecycle"/>), derives the
/// rate each needs (<see cref="RecheckRate"/>), shares each pool (<see cref="RecheckBudget"/>) and
/// issues the snapshot (<see cref="PolicySnapshotIssuer"/>) — in that order, from what the ledger
/// shows and what people set, with nothing kept between calls. Storing standings is the caller's.
/// </summary>
/// <remarks>
/// <para>
/// A release that awaits approval keeps the old standing in the snapshot; once approved, the caller
/// commits <see cref="StandingChange.Standing"/> and issues again. Tightening is committed at once.
/// </para>
/// <para>
/// Active tiers draw at the active rate, and those rechecks come out of their pool first; stable
/// tiers share the rest, the largest possible misses first. A tier's possible misses are read from
/// its window: the miss-rate upper bound u over C caught decisions bounds the misses at u·C/(1 − u);
/// a tier without the bound is unmeasured and is funded first.
/// </para>
/// </remarks>
public static class TrustController
{
    /// <summary>Closes the window ending at <paramref name="now"/> and issues snapshot <paramref name="version"/>.</summary>
    public static ControllerStep Close(
        string version, DateTimeOffset now, IReadOnlyList<SliceWindow> slices, ControllerPolicy policy, string? sliceKeyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(slices);
        ArgumentNullException.ThrowIfNull(policy);
        if (slices.GroupBy(s => (s.TierId, s.SliceKey)).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            throw new ArgumentException($"Tier '{repeated.Key.TierId}' in slice '{repeated.Key.SliceKey}' is closed twice.", nameof(slices));
        if (slices.Select(s => s.Pool).FirstOrDefault(p => !policy.PoolBudgets.ContainsKey(p)) is { } unknown)
            throw new ArgumentException($"Pool '{unknown}' has no budget.", nameof(slices));

        var judged = slices.Select(s =>
        {
            s.Freeze?.Validate();
            var step = TrustLifecycle.Release(s.Standing, s.Window, s.TeacherQualified, s.Signals, policy.Lifecycle, now);
            var held = s.Freeze is not null && Loosens(s.Standing, step.Standing);
            if (held)
                step = new StandingChange(s.Standing.State, s.Standing.State, $"{s.Freeze!.Describe()}; held: {step.Reason}", false, s.Standing);
            var committed = step.AwaitsApproval ? s.Standing : step.Standing;
            return (Slice: s, Step: step, Committed: committed, Need: RecheckRate.Solve(policy.Lifecycle.Targets, s.Traffic), Held: held);
        }).ToList();

        var pools = new Dictionary<string, BudgetAllocation>(StringComparer.Ordinal);
        var grants = new Dictionary<(string, string), BudgetGrant>();
        foreach (var pool in judged.GroupBy(j => j.Slice.Pool, StringComparer.Ordinal))
        {
            var activeSpend = pool.Where(j => j.Committed.State == TrustState.Active)
                .Sum(j => policy.Issue.ActiveRate * j.Slice.Traffic.Decisions);
            var requests = pool.Where(j => j.Committed.State == TrustState.Stable)
                .Select(j => new BudgetRequest(Key(j.Slice), j.Slice.Traffic.Decisions, j.Need, Exposure(j.Slice.Window)))
                .ToList();
            var allocation = RecheckBudget.Allocate(Math.Max(0, policy.PoolBudgets[pool.Key] - activeSpend), requests, policy.Lifecycle.Targets.RateFloor);
            pools[pool.Key] = allocation with { Budget = policy.PoolBudgets[pool.Key], Spent = allocation.Spent + activeSpend };
            var raised = 0.0;
            foreach (var grant in allocation.Grants)
            {
                var slice = pool.First(j => Key(j.Slice) == grant.SliceKey).Slice;
                var issued = grant with { SliceKey = slice.SliceKey };
                // A frozen slice is not rechecked less than its hold says; the extra comes out of the pool's spending.
                if (slice.Freeze?.MinimumRate is { } floor && issued.Rate < floor)
                {
                    var extra = (floor - issued.Rate) * slice.Traffic.Decisions;
                    issued = issued with { Rate = floor, Rechecks = issued.Rechecks + extra, SetBy = SliceFreeze.RateTerm };
                    raised += extra;
                }
                grants[(slice.TierId, slice.SliceKey)] = issued;
            }
            if (raised > 0)
                pools[pool.Key] = pools[pool.Key] with { Spent = pools[pool.Key].Spent + raised };
        }

        var snapshot = PolicySnapshotIssuer.Issue(version, now,
            judged.Select(j => new SliceIssue(j.Slice.TierId, j.Slice.SliceKey, j.Committed, grants.GetValueOrDefault((j.Slice.TierId, j.Slice.SliceKey)))
            {
                OtherKnobRates = j.Slice.OtherKnobRates,
                Selection = j.Slice.Selection,
                AbstainQuantile = ConfidenceAbstain.Quantile(j.Slice.Window, j.Slice.AbstainQuantile, j.Slice.Calibrated),
            }).ToList(),
            policy.Issue, sliceKeyVersion);

        var outcomes = judged
            .Select(j => new SliceOutcome(j.Slice.TierId, j.Slice.SliceKey, j.Step, j.Committed, j.Need, grants.GetValueOrDefault((j.Slice.TierId, j.Slice.SliceKey)))
            {
                Freeze = j.Slice.Freeze,
                Held = j.Held || (j.Slice.Freeze?.MinimumRate is not null && grants.GetValueOrDefault((j.Slice.TierId, j.Slice.SliceKey))?.SetBy == SliceFreeze.RateTerm),
            })
            .ToList();
        return new ControllerStep(snapshot, outcomes, pools);
    }

    private static string Key(SliceWindow s) => s.TierId + "\u001f" + s.SliceKey;

    // Whether `next` is further along toward release than `current`: a higher state, or more windows toward stable in the
    // same one. A demotion, or a run that starts over, is tightening and is never held.
    private static bool Loosens(SliceStanding current, SliceStanding next)
        => next.State > current.State
            || (next.State == current.State && string.Equals(next.TierVersion, current.TierVersion, StringComparison.Ordinal)
                && next.PassingWindows > current.PassingWindows);

    /// <summary>Upper bound on the must-catch decisions a tier may be missing per window: u·C/(1 − u); null when unmeasured.</summary>
    public static double? Exposure(LedgerCell window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.MissRateUpper is not { } u)
            return null;
        return u >= 1 ? double.PositiveInfinity : u * window.Caught / (1 - u);
    }
}

/// <summary>
/// Whether a tier's confidence has earned it the right to abstain on its least confident answers.
/// </summary>
/// <remarks>
/// Confidence is not a qualification, but its <i>ranking</i> can be: on one measured set a fixed
/// threshold sent up a fifth of the errors while the bottom 5% by rank held almost all of them. So a
/// tier may abstain on its lowest quantile q — sending those decisions up, which never biases the
/// ledger since the tier above judges every one of them — but only where the ledger shows the ranking
/// works: the confidence-rank AUC's lower bound (Hanley–McNeil, on the window's effective agreed and
/// disagreed counts) above chance. Where confidence runs against correctness, as it can for an
/// unsupervised anomaly score, the bound stays under 0.5 and the tier does not abstain.
/// </remarks>
public static class ConfidenceAbstain
{
    /// <summary>
    /// <paramref name="requested"/> when <paramref name="window"/> shows the tier's confidence ranks its
    /// mistakes above chance at <paramref name="confidence"/> and the tier is <paramref name="calibrated"/>; otherwise null.
    /// </summary>
    public static double? Quantile(LedgerCell window, double? requested, bool calibrated = true, double confidence = TrustLedger.DefaultConfidence)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (requested is not { } q)
            return null;
        if (!(q > 0 && q < 1))
            throw new ArgumentOutOfRangeException(nameof(requested), q, "An abstain quantile lies strictly between 0 and 1.");
        return calibrated && RankAucLower(window, confidence) > 0.5 ? q : null;
    }

    /// <summary>
    /// One-sided lower bound on <see cref="LedgerCell.ConfidenceRankAuc"/> by the Hanley–McNeil standard
    /// error, on effective counts of agreed and disagreed rechecks; null without an AUC or with fewer than
    /// one effective recheck of either kind.
    /// </summary>
    public static double? RankAucLower(LedgerCell window, double confidence = TrustLedger.DefaultConfidence)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ConfidenceRankAuc is not { } a || window.Agreement is not { } agreement)
            return null;
        var disagreed = window.EffectiveSampleSize * (1 - agreement);
        var agreed = window.EffectiveSampleSize * agreement;
        if (disagreed < 1 || agreed < 1)
            return null;
        var q1 = a / (2 - a);
        var q2 = 2 * a * a / (1 + a);
        var variance = (a * (1 - a) + (disagreed - 1) * (q1 - a * a) + (agreed - 1) * (q2 - a * a)) / (disagreed * agreed);
        return a - TrustLedger.NormalQuantile(confidence) * Math.Sqrt(Math.Max(variance, 0));
    }
}
