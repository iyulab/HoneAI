using System;
using System.Collections.Generic;
using HoneAI.Routing;

namespace HoneAI.Learning;

/// <summary>
/// How many confirmed labels are held out for measuring the tier that learns from the rest — a share of decisions,
/// overall or per slice. Which decisions are held out is fixed by a hash of the decision id, the same way a recheck
/// draw is: independent of every answer, the same on every site and every retry, and the same for every label a
/// decision gets, so a later, better label for a held-out decision is held out too.
/// </summary>
/// <remarks>
/// When the unit that must not straddle the two shares is bigger than a decision — decisions with the same normalized
/// input, the same document, the same session — the caller names it per decision (<c>splitKey</c> of
/// <see cref="Assign"/>) and the hash is of that key instead: every decision with one key falls in one share, and
/// the probability <c>q</c> is the probability of the key. Pass the same key at every call for a decision
/// (<see cref="LabelConfirmation.Decide{TValue}"/> and <see cref="LabelConfirmation.Settle{TValue}"/>), and keep a key
/// inside one slice: a key whose decisions have different slice shares can fall in different shares.
/// </remarks>
/// <remarks>
/// Every decision has a chance of each share, the hard ones included: a slice cannot be held out whole
/// (<see cref="EvaluationShare"/> stays under 1), or the learning tier would never see it. Each share carries its
/// probability on the label (<see cref="ConfirmedLabel{TValue}.ShareProbability"/>), so its weights stand for every
/// decision.
/// </remarks>
public sealed record LabelSplit
{
    /// <summary>Nothing held out: every label is for learning.</summary>
    public static LabelSplit None { get; } = new() { EvaluationShare = 0 };

    /// <summary>The share of decisions held out, in [0, 1).</summary>
    public required double EvaluationShare { get; init; }

    /// <summary>The held-out share of particular slices, each in [0, 1); <see cref="EvaluationShare"/> for any other.</summary>
    public IReadOnlyDictionary<string, double>? BySlice { get; init; }

    /// <summary>
    /// What the hash is keyed with besides the decision id. Changing it re-deals every decision — a label already
    /// learned from may land in the held-out share — so a split keeps its salt.
    /// </summary>
    public string Salt { get; init; } = "evaluation";

    /// <summary>
    /// What the split keys are, when callers pass <c>splitKey</c> (<c>normalized-text</c>, <c>session</c>) — HoneAI does
    /// not read it; the manifest records it so two training sets are known to be dealt by the same kind of unit.
    /// Null when the decision is the unit.
    /// </summary>
    public string? KeyKind { get; init; }

    /// <summary>The held-out share of <paramref name="sliceKey"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A share outside [0, 1).</exception>
    public double EvaluationShareOf(string? sliceKey)
    {
        var share = sliceKey is not null && BySlice?.TryGetValue(sliceKey, out var bySlice) == true ? bySlice : EvaluationShare;
        if (!(share >= 0 && share < 1))
            throw new ArgumentOutOfRangeException(nameof(sliceKey), share,
                "A held-out share is in [0, 1): holding out every decision of a slice leaves the learning tier nothing to learn from there.");
        return share;
    }

    /// <summary>
    /// The share the labels of <paramref name="decisionId"/> fall in, and its probability — dealt by
    /// <paramref name="splitKey"/> when the decision has one, so every decision with that key falls in the same share.
    /// </summary>
    /// <param name="decisionId">The decision.</param>
    /// <param name="sliceKey">The decision's slice.</param>
    /// <param name="splitKey">The unit the decision belongs to; null when the decision itself is the unit.</param>
    /// <exception cref="ArgumentException">A <paramref name="splitKey"/> that is empty or white space.</exception>
    public (LabelShare Share, double Probability) Assign(string decisionId, string? sliceKey, string? splitKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        if (splitKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(splitKey);
        var q = EvaluationShareOf(sliceKey);
        if (q == 0)
            return (LabelShare.Training, 1.0);
        return RecheckDraw.Uniform(splitKey ?? decisionId, Salt) < q
            ? (LabelShare.Evaluation, q)
            : (LabelShare.Training, 1 - q);
    }
}
