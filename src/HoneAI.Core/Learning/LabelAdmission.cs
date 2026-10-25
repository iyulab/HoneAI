using System;

namespace HoneAI.Learning;

/// <summary>
/// Which confirmations may become labels, and how far an unmeasured judge is trusted — consumer-set values that
/// <see cref="LabelConfirmation"/> enforces. A label it does not admit comes back as
/// <see cref="ConfirmationOutcome.NotAdmitted"/>, with the reason.
/// </summary>
public sealed record LabelAdmission
{
    /// <summary>Every confirmation is admitted, at the trust it has.</summary>
    public static LabelAdmission All { get; } = new();

    /// <summary>The least trust a label is admitted at, in [0, 1] — after <see cref="DefaultTrustCap"/>.</summary>
    public double MinimumTrust { get; init; }

    /// <summary>
    /// The most an unmeasured judge (<see cref="LabelTrustBasis.Default"/>) is trusted, in (0, 1] — a kind's default is
    /// an order of the kinds, so it can be held under what a measured judge earns. Null: the default as it is.
    /// </summary>
    public double? DefaultTrustCap { get; init; }

    /// <summary>
    /// Whether a judge may confirm labels at all — for a teacher, typically that its must-catch recall clears the floor
    /// (<c>TeacherLedger.Qualifies</c>). Null: every judge may. A consensus is judged by <see cref="MinimumTrust"/> only.
    /// </summary>
    public Func<JudgeIdentity, bool>? Qualified { get; init; }

    internal (double Value, LabelTrustBasis Basis) Cap((double Value, LabelTrustBasis Basis) trust)
    {
        if (DefaultTrustCap is { } cap && !(cap > 0 && cap <= 1))
            throw new ArgumentOutOfRangeException(nameof(DefaultTrustCap), cap, "A trust cap is in (0, 1].");
        return trust.Basis == LabelTrustBasis.Default && DefaultTrustCap is { } c && trust.Value > c ? (c, trust.Basis) : trust;
    }

    internal string? Refusal(JudgeIdentity? judge, double trust)
    {
        if (!(MinimumTrust >= 0 && MinimumTrust <= 1))
            throw new ArgumentOutOfRangeException(nameof(MinimumTrust), MinimumTrust, "A minimum trust is in [0, 1].");
        if (judge is not null && Qualified is { } qualified && !qualified(judge))
            return $"'{judge}' is not qualified to confirm labels";
        if (trust < MinimumTrust)
            return $"trust {trust:0.###} is under the minimum {MinimumTrust:0.###}" + (judge is null ? string.Empty : $" for '{judge}'");
        return null;
    }
}
