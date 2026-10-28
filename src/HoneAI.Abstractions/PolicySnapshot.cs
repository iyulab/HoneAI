using System;
using System.Collections.Generic;

namespace HoneAI;

/// <summary>Where a tier stands in a slice.</summary>
public enum TrustState
{
    /// <summary>The tier answers, but does not settle: its answer is recorded and the decision goes up.</summary>
    Shadow = 0,

    /// <summary>The tier settles the answers it may settle; a large share of them is rechecked.</summary>
    Active = 1,

    /// <summary>The tier settles; the recheck share has come down to what the promise needs.</summary>
    Stable = 2,
}

/// <summary>
/// The policy a judging site works from: for each tier and slice, its state, its recheck
/// probabilities and its abstain quantile. Issued centrally and carried to each site, so a site
/// that loses contact keeps judging by the last one it holds — within its expiry.
/// </summary>
public sealed record PolicySnapshot
{
    /// <summary>Version of this snapshot. Recorded on every decision made under it.</summary>
    public required string Version { get; init; }

    /// <summary>When it was issued.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// From this time on, no tier is to gain ground under this snapshot. The controller releases in one place
    /// and issues a fresh snapshot each time, so the expiry is read where a release judged earlier is applied
    /// later — by a person's approval, say: <c>SnapshotExpiry.MayApply</c> (in <c>HoneAI.Trust</c>) is that check,
    /// and a release it holds back is judged again. Which approval path applies a release is the consumer's.
    /// </summary>
    public required DateTimeOffset SoftExpiresAt { get; init; }

    /// <summary>
    /// From this time on, the snapshot is not to be trusted: the router stops settling below the top tier —
    /// decisions go up where a tier above can be reached, and where none can they are marked unverified.
    /// </summary>
    public required DateTimeOffset HardExpiresAt { get; init; }

    /// <summary>Whether <paramref name="now"/> is at or past <see cref="SoftExpiresAt"/>.</summary>
    public bool IsSoftExpired(DateTimeOffset now) => now >= SoftExpiresAt;

    /// <summary>Whether <paramref name="now"/> is at or past <see cref="HardExpiresAt"/>.</summary>
    public bool IsHardExpired(DateTimeOffset now) => now >= HardExpiresAt;

    /// <summary>Version of the slice-key definition the entries were computed under.</summary>
    public string? SliceKeyVersion { get; init; }

    /// <summary>One entry per tier and slice. A slice with no entry is in <see cref="TrustState.Shadow"/>.</summary>
    public required IReadOnlyList<PolicyEntry> Entries { get; init; }
}

/// <summary>One tier in one slice.</summary>
public sealed record PolicyEntry
{
    /// <summary>The tier's <see cref="Tier.Id"/>.</summary>
    public required string TierId { get; init; }

    /// <summary>The slice key.</summary>
    public required string SliceKey { get; init; }

    /// <summary>The tier's state in the slice.</summary>
    public required TrustState State { get; init; }

    /// <summary>
    /// Recheck probability per knob (for example a uniform share and a slice-watch share) — the
    /// expected rate, which <see cref="Selection"/> may tilt per decision. The probability a settled
    /// decision is drawn with is the largest of its knobs' rates.
    /// </summary>
    public IReadOnlyDictionary<string, double>? SampleRates { get; init; }

    /// <summary>
    /// How a knob tilts its rate toward the decisions worth checking, by knob name. A knob without
    /// an entry draws every decision at its rate.
    /// </summary>
    public IReadOnlyDictionary<string, KnobSelection>? Selection { get; init; }

    /// <summary>Share of its least confident answers the tier abstains on, when set.</summary>
    public double? AbstainQuantile { get; init; }

    /// <summary>Which term set the recheck probability, for the audit (a deadline, a budget, a floor).</summary>
    public string? RateSetBy { get; init; }
}
