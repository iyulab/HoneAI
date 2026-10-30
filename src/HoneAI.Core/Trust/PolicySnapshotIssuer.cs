using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Trust;

/// <summary>How snapshots are issued. People set these.</summary>
public sealed record IssuePolicy
{
    /// <summary>How long a snapshot is fully trusted; after it, nothing gains ground under it.</summary>
    public required TimeSpan SoftLife { get; init; }

    /// <summary>How long a snapshot is trusted at all; after it, sites fail closed.</summary>
    public required TimeSpan HardLife { get; init; }

    /// <summary>The recheck probability of an active tier — high, since it has not yet shown its promise for long.</summary>
    public required double ActiveRate { get; init; }

    /// <summary>
    /// The knob the promise is counted on, and the one the controller's rate goes to. Other knobs
    /// (watching slices, ranking confidence) find and harvest; this one measures.
    /// </summary>
    public string PromiseKnob { get; init; } = "uniform";
}

/// <summary>One tier in one slice, ready to be issued.</summary>
/// <param name="TierId">The tier.</param>
/// <param name="SliceKey">The slice.</param>
/// <param name="Standing">Its committed standing — a release still awaiting approval is not one.</param>
/// <param name="StableGrant">Its share of the pool, for a stable tier (<see cref="RecheckBudget"/>).</param>
public sealed record SliceIssue(string TierId, string SliceKey, SliceStanding Standing, BudgetGrant? StableGrant = null)
{
    /// <summary>Rates of other knobs, kept beside the promise knob's.</summary>
    public IReadOnlyDictionary<string, double>? OtherKnobRates { get; init; }

    /// <summary>How knobs tilt toward decisions worth checking (<see cref="SelectionFit"/>).</summary>
    public IReadOnlyDictionary<string, KnobSelection>? Selection { get; init; }

    /// <summary>The share of least confident answers to abstain on — only where the ledger showed confidence ranks mistakes.</summary>
    public double? AbstainQuantile { get; init; }

    /// <summary>The expected loss at or above which the tier hands a decision up (<see cref="ExpectedLoss.Line"/>); null leaves it off.</summary>
    public double? AbstainExpectedLossAbove { get; init; }
}

/// <summary>
/// Turns the controller's standings and rates into the <see cref="PolicySnapshot"/> sites judge by.
/// </summary>
/// <remarks>
/// A shadow tier is issued with no rates: every one of its decisions goes up, so there is nothing to
/// draw. An active tier's promise knob runs at <see cref="IssuePolicy.ActiveRate"/>; a stable tier's
/// at its pool grant. Every entry says what set its rate (<see cref="PolicyEntry.RateSetBy"/>), so
/// "why this π" is answered from the snapshot itself.
/// </remarks>
public static class PolicySnapshotIssuer
{
    /// <summary>What set an active tier's rate.</summary>
    public const string ActiveRateTerm = "active";

    /// <summary>Issues a snapshot.</summary>
    public static PolicySnapshot Issue(
        string version, DateTimeOffset now, IReadOnlyList<SliceIssue> slices, IssuePolicy policy, string? sliceKeyVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(slices);
        ArgumentNullException.ThrowIfNull(policy);
        if (!(policy.SoftLife > TimeSpan.Zero && policy.SoftLife <= policy.HardLife))
            throw new ArgumentOutOfRangeException(nameof(policy), "A snapshot needs 0 < soft life ≤ hard life.");
        if (!(policy.ActiveRate > 0 && policy.ActiveRate <= 1))
            throw new ArgumentOutOfRangeException(nameof(policy), policy.ActiveRate, "The active rate lies in (0, 1].");
        if (slices.GroupBy(s => (s.TierId, s.SliceKey)).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            throw new ArgumentException($"Tier '{repeated.Key.TierId}' in slice '{repeated.Key.SliceKey}' is issued twice.", nameof(slices));

        return new PolicySnapshot
        {
            Version = version,
            IssuedAt = now,
            SoftExpiresAt = now + policy.SoftLife,
            HardExpiresAt = now + policy.HardLife,
            SliceKeyVersion = sliceKeyVersion,
            Entries = slices.Select(s => Entry(s, policy)).ToList(),
        };
    }

    private static PolicyEntry Entry(SliceIssue slice, IssuePolicy policy)
    {
        var (rate, setBy) = slice.Standing.State switch
        {
            TrustState.Shadow => ((double?)null, (string?)null),
            TrustState.Active => (policy.ActiveRate, ActiveRateTerm),
            _ => slice.StableGrant is { } grant
                ? (grant.Rate, grant.SetBy)
                : throw new ArgumentException(
                    $"Stable tier '{slice.TierId}' in slice '{slice.SliceKey}' has no pool grant; a stable tier is drawn at the rate it was granted.",
                    nameof(slice)),
        };
        if (rate is not null && slice.OtherKnobRates?.ContainsKey(policy.PromiseKnob) == true)
            throw new ArgumentException($"Knob '{policy.PromiseKnob}' carries the controller's rate and cannot be set by hand.", nameof(slice));

        Dictionary<string, double>? rates = null;
        if (rate is { } r)
        {
            rates = new Dictionary<string, double>(StringComparer.Ordinal) { [policy.PromiseKnob] = r };
            foreach (var (knob, other) in slice.OtherKnobRates ?? new Dictionary<string, double>())
                rates[knob] = other;
        }
        return new PolicyEntry
        {
            TierId = slice.TierId,
            SliceKey = slice.SliceKey,
            State = slice.Standing.State,
            SampleRates = rates,
            Selection = rate is null ? null : slice.Selection,
            AbstainQuantile = slice.Standing.State == TrustState.Shadow ? null : slice.AbstainQuantile,
            AbstainExpectedLossAbove = slice.Standing.State == TrustState.Shadow ? null : slice.AbstainExpectedLossAbove,
            RateSetBy = setBy,
        };
    }
}
