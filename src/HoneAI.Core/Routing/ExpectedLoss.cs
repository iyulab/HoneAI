using System;
using System.Collections.Generic;
using System.Linq;

namespace HoneAI.Routing;

/// <summary>
/// The expected loss of letting a tier settle a decision — its weight times the calibrated probability the tier is
/// wrong — and the line above which a tier hands such decisions up (<see cref="PolicyEntry.AbstainExpectedLossAbove"/>).
/// It is the same score that tilts the recheck draw (<see cref="SelectionScore"/>) used for settling: a decision
/// that would cost a lot if wrong goes up before it is settled, instead of being checked after.
/// </summary>
public static class ExpectedLoss
{
    /// <summary>
    /// The expected loss of <paramref name="signal"/> under a snapshot entry's calibration, or null when it cannot be
    /// told — the entry carries no calibration yet, or the decision has no risk (a decision without a risk is not
    /// handed up for one it never had). The calibration is the first knob's (by name) that has one.
    /// </summary>
    public static double? Of(PolicyEntry entry, SelectionSignal? signal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Selection is not { Count: > 0 } selections)
            return null;
        var calibration = selections.OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => s.Value.Calibration).FirstOrDefault(c => c.Count > 0);
        return calibration is null ? null : Of(calibration, signal);
    }

    /// <summary>The expected loss of <paramref name="signal"/> under <paramref name="calibration"/>: <c>weight × P(wrong at risk)</c>.</summary>
    public static double? Of(IReadOnlyList<RiskBin> calibration, SelectionSignal? signal)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (signal?.Risk is not { } risk || SelectionScore.Calibrate(calibration, risk) is not { } p)
            return null;
        return Math.Max(signal.Weight, 0) * p;
    }

    /// <summary>
    /// The line that hands up about <paramref name="abstainShare"/> of the decisions in <paramref name="losses"/>: the
    /// smallest loss that at least that share reach (ties may hand up more). The caller passes the expected losses of
    /// a slice's recent decisions — their signals are in each decision's provenance — and puts the line in the
    /// controller's input. Null when there are no losses or the share is not in (0, 1).
    /// </summary>
    public static double? Line(IEnumerable<double> losses, double abstainShare)
    {
        ArgumentNullException.ThrowIfNull(losses);
        if (!(abstainShare > 0 && abstainShare < 1))
            return null;
        var sorted = losses.Where(double.IsFinite).OrderBy(l => l).ToArray();
        if (sorted.Length == 0)
            return null;
        return sorted[(int)Math.Floor((1 - abstainShare) * sorted.Length)];
    }
}
