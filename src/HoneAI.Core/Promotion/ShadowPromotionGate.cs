using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoneAI.Trust;

namespace HoneAI;

/// <summary>
/// A shadow's earned standing decides: the candidate runs as a shadow version of the learning tier — answering
/// beside production, its answers rechecked, settling nothing — and is promoted once the trust lifecycle has released
/// it in every slice it is watched in (<see cref="TrustLifecycle.Release"/>). Until then the run waits; a candidate
/// that has not earned its standing within <see cref="MaximumShadow"/> of training is refused.
/// </summary>
/// <remarks>
/// The gate reads standings; it does not move them. Releasing a shadow is the lifecycle's — and while
/// <see cref="LifecyclePolicy.AutoRelease"/> is off, a release waits for a person's approval before the consumer
/// stores the new standing. So with auto-release off this gate promotes only what a person let out of shadow, on the
/// ledger's numbers; with it on, nothing in the loop waits for anyone.
/// </remarks>
public sealed class ShadowPromotionGate : IPromotionGate
{
    private readonly Func<PromotionCandidate, CancellationToken, Task<IReadOnlyCollection<SliceStanding>>> _standings;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="standings">
    /// The candidate's standings, one per slice it is watched in — the consumer keeps them, as it keeps every standing.
    /// Standings of another version are ignored.
    /// </param>
    /// <param name="maximumShadow">How long after training the candidate may take to earn its standing.</param>
    /// <param name="versionOf">The tier version the candidate runs as; its experiment id when null.</param>
    /// <param name="clock">Now; <see cref="DateTimeOffset.UtcNow"/> when null.</param>
    public ShadowPromotionGate(
        Func<PromotionCandidate, CancellationToken, Task<IReadOnlyCollection<SliceStanding>>> standings,
        TimeSpan maximumShadow,
        Func<PromotionCandidate, string>? versionOf = null,
        Func<DateTimeOffset>? clock = null)
    {
        _standings = standings ?? throw new ArgumentNullException(nameof(standings));
        if (maximumShadow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumShadow), maximumShadow, "A shadow needs time to earn its standing.");
        MaximumShadow = maximumShadow;
        VersionOf = versionOf ?? (c => c.ExperimentId);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How long after training the candidate may take to earn its standing.</summary>
    public TimeSpan MaximumShadow { get; }

    /// <summary>The tier version a candidate runs as.</summary>
    public Func<PromotionCandidate, string> VersionOf { get; }

    /// <inheritdoc />
    public async Task<PromotionVerdict> JudgeAsync(PromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var version = VersionOf(candidate);
        var standings = (await _standings(candidate, cancellationToken).ConfigureAwait(false))
            .Where(s => string.Equals(s.TierVersion, version, StringComparison.Ordinal))
            .ToList();

        if (standings.Count > 0 && standings.All(s => s.State != TrustState.Shadow))
            return new(PromotionDecision.Promote,
                $"{version} earned its standing in shadow: {Describe(standings)}", JudgeKind.Statistics);

        var since = candidate.TrainedAt ?? standings.Select(s => (DateTimeOffset?)s.Since).Min();
        if (since is { } start && _clock() - start > MaximumShadow)
            return new(PromotionDecision.Refuse,
                $"{version} did not earn its standing within {MaximumShadow} of training"
                + (standings.Count == 0 ? " — it was never watched in shadow" : $": {Describe(standings)}"), JudgeKind.Statistics);

        return new(PromotionDecision.Wait,
            standings.Count == 0 ? $"{version} is not watched in shadow yet" : $"{version} in shadow: {Describe(standings)}",
            JudgeKind.Statistics);
    }

    private static string Describe(IEnumerable<SliceStanding> standings)
        => string.Join(", ", standings.GroupBy(s => s.State).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
}
