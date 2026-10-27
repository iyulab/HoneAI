using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Learning;

/// <summary>
/// What the caller requires before a person's answer to one decision of a cluster may be spread to the others. HoneAI
/// fixes none of these numbers: how many decisions to check, how much agreement is enough and how much a spread label may
/// weigh depend on the data, and a cluster that is not pure is worse spread than left alone.
/// </summary>
/// <param name="MinimumChecked">K — the fewest decisions of the cluster a person must have checked, at least 1.</param>
/// <param name="MinimumAgreement">The lowest share of the checked decisions the representative's value must be right for, in (0, 1].</param>
/// <param name="WeightCap">The most a spread label may weigh, in (0, 1] — like a voluntary label it has no π to divide by.</param>
public sealed record PropagationPolicy(int MinimumChecked, double MinimumAgreement, double WeightCap)
{
    /// <summary>Checks the numbers.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A number is out of its range.</exception>
    public void Validate()
    {
        if (MinimumChecked < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumChecked), MinimumChecked, "At least one decision must be checked.");
        if (!(MinimumAgreement > 0 && MinimumAgreement <= 1))
            throw new ArgumentOutOfRangeException(nameof(MinimumAgreement), MinimumAgreement, "The required agreement is in (0, 1].");
        if (!(WeightCap > 0 && WeightCap <= 1))
            throw new ArgumentOutOfRangeException(nameof(WeightCap), WeightCap, "A spread label's weight cap is in (0, 1].");
    }
}

/// <summary>What checking part of a cluster showed about how far one answer holds for the rest.</summary>
/// <param name="Checked">Decisions of the cluster a person checked beside the representative.</param>
/// <param name="Agreed">Of those, the ones the representative's value is right for.</param>
/// <param name="Passed">Whether the check met the policy.</param>
/// <param name="Reason">Why not, when it did not.</param>
public sealed record PurityCheck(int Checked, int Agreed, bool Passed, string? Reason = null)
{
    /// <summary>The observed agreement — the purity estimate a spread label's weight is multiplied by; 0 when nothing was checked.</summary>
    public double Agreement => Checked == 0 ? 0 : (double)Agreed / Checked;
}

/// <summary>
/// Spreading a person's answer over a cluster of decisions (a question card asked once for many), as labels that teach and
/// never measure: the value is an estimate of what a person would say, not something anyone saw.
/// </summary>
public static class ClusterPropagation
{
    /// <summary>
    /// Whether the representative's value holds well enough for the cluster to take it: each entry of
    /// <paramref name="agreements"/> is one decision a person checked apart from the representative — <see langword="true"/>
    /// when the representative's value is right for it. A cluster passes when at least <see cref="PropagationPolicy.MinimumChecked"/>
    /// decisions were checked and the agreement reaches <see cref="PropagationPolicy.MinimumAgreement"/>.
    /// </summary>
    /// <param name="agreements">The checked decisions' agreement with the representative.</param>
    /// <param name="policy">What the caller requires.</param>
    public static PurityCheck Assess(IReadOnlyCollection<bool> agreements, PropagationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(agreements);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var check = new PurityCheck(agreements.Count, agreements.Count(a => a), Passed: false);
        if (check.Checked < policy.MinimumChecked)
            return check with { Reason = $"Only {check.Checked} decision(s) of the cluster were checked; {policy.MinimumChecked} are required." };
        if (check.Agreement < policy.MinimumAgreement)
            return check with { Reason = $"The representative's value held for {check.Agreement:P0} of the checked decisions; {policy.MinimumAgreement:P0} is required." };
        return check with { Passed = true };
    }

    /// <summary>
    /// The label a person's answer to <paramref name="representative"/> makes for another decision of its cluster, when the
    /// cluster passed its purity check. It is a <see cref="LabelOrigin.Propagated"/> label: in <see cref="LabelShare.Training"/> at
    /// the representative's trust times the cluster's agreement, capped by <see cref="PropagationPolicy.WeightCap"/>, with no π
    /// (it stands for itself, never for the decisions nobody looked at); a decision the split holds out for measuring takes none
    /// (<see cref="ConfirmationOutcome.NotAdmitted"/> — teaching on it would put the held-out set in the training one); and it is
    /// counted in nobody's measured trust (the label says <see cref="ConfirmedLabel{TValue}.Origin"/>).
    /// </summary>
    /// <param name="representative">The label a person's answer made for the cluster's representative.</param>
    /// <param name="decisionId">The decision to spread it to — not the representative.</param>
    /// <param name="sliceKey">That decision's slice, when it has one.</param>
    /// <param name="purity">What checking the cluster showed (<see cref="Assess"/>).</param>
    /// <param name="policy">What the caller requires; the cap on the weight.</param>
    /// <param name="confirmedAt">When the label is made.</param>
    /// <param name="annotations">The references the sinks need for <paramref name="decisionId"/>, as in <c>LabelConfirmation.Decide</c>.</param>
    /// <param name="split">Which decisions are held out; <see cref="LabelSplit.None"/> when null.</param>
    /// <param name="splitKey">The split key of the decision, as in <c>LabelConfirmation.Decide</c>.</param>
    public static Confirmation<TValue> Propagate<TValue>(
        ConfirmedLabel<TValue> representative,
        string decisionId,
        string? sliceKey,
        PurityCheck purity,
        PropagationPolicy policy,
        DateTimeOffset confirmedAt,
        IReadOnlyDictionary<string, string>? annotations = null,
        LabelSplit? split = null,
        string? splitKey = null)
    {
        ArgumentNullException.ThrowIfNull(representative);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentNullException.ThrowIfNull(purity);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        if (string.Equals(decisionId, representative.DecisionId, StringComparison.Ordinal))
            return new(ConfirmationOutcome.NotALabel, Reason: "The representative's own label is not spread to itself.");
        if (representative.Origin == LabelOrigin.Propagated)
            return new(ConfirmationOutcome.NotALabel, Reason: "A propagated label is an estimate; spreading an estimate again compounds it.");
        if (representative.Share == LabelShare.Evaluation)
            return new(ConfirmationOutcome.NotALabel, Reason: "The representative's label is held out for measuring; nothing learned from it may be spread.");
        if (!purity.Passed)
            return new(ConfirmationOutcome.NotAdmitted, Reason: purity.Reason ?? "The cluster did not pass its purity check.");

        var (share, _) = (split ?? LabelSplit.None).Assign(decisionId, sliceKey, splitKey);
        if (share == LabelShare.Evaluation)
            return new(ConfirmationOutcome.NotAdmitted,
                Reason: $"Decision '{decisionId}' is held out for measuring the learner; a spread label is not a measurement and must not teach on it.");

        return new(ConfirmationOutcome.Confirmed, new ConfirmedLabel<TValue>
        {
            DecisionId = decisionId,
            SliceKey = sliceKey,
            Value = representative.Value,
            ConfirmedBy = representative.ConfirmedBy,
            JudgedBy = representative.JudgedBy,
            Participants = representative.Participants,
            Kind = representative.Kind,
            ConfirmedByVersion = representative.ConfirmedByVersion,
            SnapshotVersion = representative.SnapshotVersion,
            Probability = 1.0,
            Trust = representative.Trust,
            TrustBasis = representative.TrustBasis,
            ConfirmedAt = confirmedAt,
            Share = LabelShare.Training,
            ShareProbability = 1.0,
            SplitKey = splitKey,
            Origin = LabelOrigin.Propagated,
            Purity = purity.Agreement,
            PropagatedFrom = representative.DecisionId,
            WeightCap = policy.WeightCap,
            Annotations = annotations,
        });
    }
}
