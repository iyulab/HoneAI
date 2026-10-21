using System;

namespace HoneAI.Trust;

/// <summary>
/// The rules a tier's standing in a slice moves by. People set every number here; the lifecycle
/// only applies them.
/// </summary>
public sealed record LifecyclePolicy
{
    /// <summary>The promise — agreement p0 and the must-catch miss limit m are read from it.</summary>
    public required TrustTargets Targets { get; init; }

    /// <summary>
    /// The margin ε: release needs the agreement's lower bound at p0 + ε, demotion its upper bound
    /// under p0 − ε. The gap between them keeps a slice on the boundary from flapping.
    /// </summary>
    public required double Margin { get; init; }

    /// <summary>Effective sample size a window must reach before it can release anything.</summary>
    public required double MinimumEffectiveSample { get; init; }

    /// <summary>Share of rechecks the tier above may fail to answer in a window that releases.</summary>
    public double MaximumUpperFailureRate { get; init; } = 0.1;

    /// <summary>Time a tier stays in a state before it can be released from it (T).</summary>
    public required TimeSpan MinimumDwell { get; init; }

    /// <summary>Consecutive releasing windows an active tier needs to become stable (K).</summary>
    public int StableWindows { get; init; } = 3;

    /// <summary>
    /// Whether a release may take effect without a person. Off until the consumer's shadow
    /// measurement has shown the controller's numbers; until then every release waits for approval.
    /// Tightening never waits.
    /// </summary>
    public bool AutoRelease { get; init; }
}

/// <summary>Where a tier stands in a slice, and since when.</summary>
/// <param name="State">The state.</param>
/// <param name="Since">When it entered the state.</param>
/// <param name="TierVersion">The tier's version the standing was earned with; a new version starts over.</param>
/// <param name="PassingWindows">Consecutive releasing windows while active.</param>
public sealed record SliceStanding(TrustState State, DateTimeOffset Since, string TierVersion, int PassingWindows = 0)
{
    /// <summary>A new slice, or a tier's new version: shadow from <paramref name="now"/>.</summary>
    public static SliceStanding New(string tierVersion, DateTimeOffset now) => new(TrustState.Shadow, now, tierVersion);
}

/// <summary>
/// One judgement of a standing: where it goes and why. <see cref="AwaitsApproval"/> marks a release
/// that may not take effect until a person approves it — <see cref="Standing"/> is then the standing
/// it would become.
/// </summary>
public sealed record LifecycleStep(TrustState From, TrustState To, string Reason, bool AwaitsApproval, SliceStanding Standing)
{
    /// <summary>The state moved.</summary>
    public bool Moved => From != To;
}

/// <summary>Live signals that the world a tier was measured in has moved.</summary>
/// <param name="TierVersion">The tier's current version.</param>
/// <param name="Drifted">Its inputs or answers have moved (a PSI, a CUSUM, a falling recent window).</param>
public sealed record LiveSignals(string TierVersion, bool Drifted = false);

/// <summary>
/// The lifecycle of a tier in a slice — shadow, active, stable — judged on the trust ledger.
/// </summary>
/// <remarks>
/// <para>
/// Release and tightening are judged by different rules, which is what keeps them from flapping and
/// keeps the statistics honest. <b>Release</b> (<see cref="Release"/>) happens only at a window's end,
/// on a fixed-sample test over that closed window: enough effective sample, the agreement's lower
/// bound at p0 + ε, the miss rate's upper bound within m, a qualified teacher, the tier above
/// answering, and the minimum dwell passed. Shadow → active takes one such window; active → stable
/// takes <see cref="LifecyclePolicy.StableWindows"/> in a row.
/// </para>
/// <para>
/// <b>Tightening</b> (<see cref="Tighten"/>) may happen at any time, on a running cell. Looking at a
/// running cell again and again inflates a fixed-confidence bound's error, so the cell passed in
/// should be computed at a stricter confidence than release uses — a union bound over the looks
/// standing in for a confidence sequence. A new tier version → shadow. A must-catch miss shown
/// above m, or agreement shown under p0 − ε → shadow. A single seen miss, agreement shown under p0,
/// or drifted inputs → stable falls back to active, where it is rechecked at the active rate.
/// </para>
/// </remarks>
public static class TrustLifecycle
{
    /// <summary>Judges a closed window for release. Tightening is judged first: a window that tightens does not release.</summary>
    /// <param name="standing">Where the tier stands.</param>
    /// <param name="window">The ledger cell over the closed window.</param>
    /// <param name="teacherQualified">The tier above is qualified to teach it (its must-catch recall bound clears the floor).</param>
    /// <param name="signals">The tier's live signals.</param>
    /// <param name="policy">The rules.</param>
    /// <param name="now">The window's end.</param>
    public static LifecycleStep Release(
        SliceStanding standing, LedgerCell window, bool teacherQualified, LiveSignals signals, LifecyclePolicy policy, DateTimeOffset now)
    {
        var tightened = Tighten(standing, window, signals, policy, now);
        if (tightened.Moved || tightened.Standing != standing)
            return tightened;

        if (standing.State == TrustState.Stable)
            return Stay(standing, "stable; nothing to release");

        var blocked = ReleaseBlocker(standing, window, teacherQualified, policy, now);
        if (blocked is not null)
            return Stay(standing with { PassingWindows = 0 }, blocked);

        if (standing.State == TrustState.Shadow)
            return Releasing(standing, new SliceStanding(TrustState.Active, now, standing.TierVersion), "window met every release condition", policy);

        var passing = standing.PassingWindows + 1;
        return passing >= policy.StableWindows
            ? Releasing(standing, new SliceStanding(TrustState.Stable, now, standing.TierVersion), $"{passing} windows in a row met every release condition", policy)
            : Stay(standing with { PassingWindows = passing }, $"{passing} of {policy.StableWindows} windows toward stable");
    }

    /// <summary>Judges a running cell for tightening. Never waits for approval.</summary>
    public static LifecycleStep Tighten(SliceStanding standing, LedgerCell running, LiveSignals signals, LifecyclePolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(standing);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(policy);
        RecheckRate.Validate(policy.Targets);
        var p0 = policy.Targets.Agreement;
        var m = policy.Targets.MissLimit;

        if (!string.Equals(signals.TierVersion, standing.TierVersion, StringComparison.Ordinal))
            return Tightening(standing, SliceStanding.New(signals.TierVersion, now), $"tier version changed ({standing.TierVersion} → {signals.TierVersion})");
        if (standing.State == TrustState.Shadow)
            return Stay(standing, "shadow");

        if (running.MissRateLower > m)
            return Tightening(standing, new SliceStanding(TrustState.Shadow, now, standing.TierVersion), $"must-catch miss rate shown above {m:P1} (lower bound {running.MissRateLower:P1})");
        if (running.AgreementUpper < p0 - policy.Margin)
            return Tightening(standing, new SliceStanding(TrustState.Shadow, now, standing.TierVersion), $"agreement shown under {p0 - policy.Margin:P1} (upper bound {running.AgreementUpper:P1})");

        if (standing.State == TrustState.Stable)
        {
            if (running.MissesEstimated > 0)
                return Tightening(standing, new SliceStanding(TrustState.Active, now, standing.TierVersion), "a must-catch miss was seen");
            if (running.AgreementUpper < p0)
                return Tightening(standing, new SliceStanding(TrustState.Active, now, standing.TierVersion), $"agreement shown under {p0:P1} (upper bound {running.AgreementUpper:P1})");
            if (signals.Drifted)
                return Tightening(standing, new SliceStanding(TrustState.Active, now, standing.TierVersion), "inputs or answers drifted");
        }
        else if (running.MissesEstimated > 0 && standing.PassingWindows > 0)
        {
            // An active tier is already rechecked at the active rate; a seen miss restarts its run toward stable.
            return Stay(standing with { PassingWindows = 0 }, "a must-catch miss was seen; the run toward stable starts over");
        }
        return Stay(standing, "no tightening condition met");
    }

    private static string? ReleaseBlocker(SliceStanding standing, LedgerCell window, bool teacherQualified, LifecyclePolicy policy, DateTimeOffset now)
    {
        var p0 = policy.Targets.Agreement;
        if (now - standing.Since < policy.MinimumDwell)
            return $"dwell {now - standing.Since} is under {policy.MinimumDwell}";
        if (window.EffectiveSampleSize < policy.MinimumEffectiveSample)
            return $"effective sample {window.EffectiveSampleSize:F0} is under {policy.MinimumEffectiveSample:F0}";
        if (!(window.AgreementLower >= p0 + policy.Margin))
            return $"agreement lower bound {window.AgreementLower:P1} is under {p0 + policy.Margin:P1}";
        if (window.MissRateUpper is null)
            return "no caught must-catch decision to bound the miss rate against";
        if (window.MissRateUpper > policy.Targets.MissLimit)
            return $"miss rate upper bound {window.MissRateUpper:P1} is over {policy.Targets.MissLimit:P1}";
        if (!teacherQualified)
            return "the tier above is not qualified to teach";
        if (window.UpperFailureRate > policy.MaximumUpperFailureRate)
            return $"the tier above failed {window.UpperFailureRate:P0} of rechecks (limit {policy.MaximumUpperFailureRate:P0})";
        return null;
    }

    private static LifecycleStep Stay(SliceStanding standing, string reason) => new(standing.State, standing.State, reason, false, standing);

    private static LifecycleStep Tightening(SliceStanding from, SliceStanding to, string reason) => new(from.State, to.State, reason, false, to);

    private static LifecycleStep Releasing(SliceStanding from, SliceStanding to, string reason, LifecyclePolicy policy)
        => new(from.State, to.State, reason, !policy.AutoRelease, to);
}

/// <summary>Reads MLoop's drift of a model as the staleness signal of the tier it serves.</summary>
public static class StalenessWatch
{
    /// <summary>
    /// Whether the model's inputs or answers moved at least to <paramref name="level"/> — in the slice
    /// value <paramref name="sliceValue"/> when MLoop reported one for it, else overall. A window MLoop
    /// could not measure is not drift: it is no evidence, and the ledger's own numbers still tighten.
    /// </summary>
    public static bool Drifted(MLoopDrift drift, MLoopDriftLevel level = MLoopDriftLevel.Significant, string? sliceValue = null)
    {
        ArgumentNullException.ThrowIfNull(drift);
        if (!drift.Measured)
            return false;
        var report = sliceValue is not null && drift.Slices?.TryGetValue(sliceValue, out var slice) == true ? slice : drift.Report!;
        return report.MovedAtLeast(level);
    }
}
