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

    /// <summary>After this, no tier gains ground under this snapshot.</summary>
    public required DateTimeOffset SoftExpiresAt { get; init; }

    /// <summary>
    /// After this, the snapshot is not trusted: stable tiers are held to active, decisions go up
    /// where a tier above can be reached, and where none can they are marked unverified.
    /// </summary>
    public required DateTimeOffset HardExpiresAt { get; init; }

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
    /// Recheck probability per knob (for example a uniform share and a slice-watch share). The
    /// probability a settled decision is drawn with is the largest of them.
    /// </summary>
    public IReadOnlyDictionary<string, double>? SampleRates { get; init; }

    /// <summary>Share of its least confident answers the tier abstains on, when set.</summary>
    public double? AbstainQuantile { get; init; }

    /// <summary>Which term set the recheck probability, for the audit (a deadline, a budget, a floor).</summary>
    public string? RateSetBy { get; init; }
}
