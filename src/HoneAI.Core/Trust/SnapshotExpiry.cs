using System;

namespace HoneAI.Trust;

/// <summary>
/// What a policy snapshot's soft expiry means where a standing change is applied. The controller releases
/// in one place (<c>Close</c>) and issues a fresh snapshot every time, so a release is judged under a
/// snapshot that is current by construction; the expiry is read later, when a release that awaited a
/// person's approval (<see cref="StandingChange.AwaitsApproval"/>) is finally applied.
/// </summary>
public static class SnapshotExpiry
{
    /// <summary>
    /// Whether <paramref name="step"/>, judged under <paramref name="snapshot"/>, may take effect at <paramref name="now"/>.
    /// A step that gains ground (shadow → active, active → stable) may not once the snapshot is soft-expired: the
    /// evidence it was judged on may have moved, so it is judged again against a fresh snapshot. A step that
    /// keeps or loses ground (staying, tightening, falling back) is never held back by an expiry — holding a
    /// tightening only leaves the tier trusted for longer.
    /// </summary>
    public static bool MayApply(PolicySnapshot snapshot, StandingChange step, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(step);
        return !GainsGround(step) || !snapshot.IsSoftExpired(now);
    }

    /// <summary>Whether <paramref name="step"/> moves a tier to a state with more trust.</summary>
    public static bool GainsGround(StandingChange step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.To > step.From;
    }
}
