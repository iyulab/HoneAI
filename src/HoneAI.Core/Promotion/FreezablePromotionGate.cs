using System;
using System.Threading;
using System.Threading.Tasks;
using HoneAI.Trust;

namespace HoneAI;

/// <summary>
/// A person's hold on a promotion: while the candidate's slice is frozen (<see cref="SliceFreeze"/>) a promotion the
/// wrapped gate would grant waits instead, and goes through once the hold is lifted. Putting a new version into
/// production is a release — the same kind of thing a freeze stops in <see cref="TrustController"/> — so the lever
/// that holds a slice's standing holds its model too. The wrapped gate is still asked, so a review is open to be decided
/// during the hold and its decision stands afterwards; a refusal passes through (keeping production is not a release),
/// and rolling back is not held (<see cref="IModelLifecycle.RollBackAsync"/> tightens).
/// </summary>
public sealed class FreezablePromotionGate : IPromotionGate
{
    private readonly IPromotionGate _inner;
    private readonly Func<PromotionCandidate, CancellationToken, Task<SliceFreeze?>> _freezeOf;

    /// <param name="inner">The gate that decides when nothing is held.</param>
    /// <param name="freezeOf">The hold on the candidate's model or slices, or null — the consumer keeps freezes, as it keeps standings.</param>
    public FreezablePromotionGate(IPromotionGate inner, Func<PromotionCandidate, CancellationToken, Task<SliceFreeze?>> freezeOf)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _freezeOf = freezeOf ?? throw new ArgumentNullException(nameof(freezeOf));
    }

    /// <inheritdoc />
    public async Task<PromotionVerdict> JudgeAsync(PromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var verdict = await _inner.JudgeAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (verdict.Decision != PromotionDecision.Promote
            || await _freezeOf(candidate, cancellationToken).ConfigureAwait(false) is not { } freeze)
            return verdict;

        freeze.Validate();
        return new(PromotionDecision.Wait,
            $"{freeze.Describe()}; held: {verdict.Reason ?? "it would be promoted"}", JudgeKind.Human)
        { By = freeze.By };
    }
}
