using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// Turns what the router produced into what the trust ledger counts: a drawn decision and its blind
/// recheck into a <see cref="RecheckRecord"/>, and an escalated decision into the tiers that
/// caught a must-catch answer.
/// </summary>
public static class RecheckRecords
{
    /// <summary>
    /// The record of <paramref name="recheck"/> on <paramref name="decision"/>. Agreement is the
    /// settlement policy's predicate, so a tolerance or a same-side-of-a-limit rule counts the same
    /// way it settles.
    /// </summary>
    /// <exception cref="ArgumentException">The decision was not drawn for recheck, or the recheck is of another decision.</exception>
    public static RecheckRecord From<TValue>(
        RoutedDecision<TValue> decision, RecheckResult<TValue> recheck, ISettlementPolicy<TValue> policy)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(recheck);
        ArgumentNullException.ThrowIfNull(policy);
        if (recheck.DecisionId != decision.DecisionId)
            throw new ArgumentException($"The recheck is of decision '{recheck.DecisionId}', not '{decision.DecisionId}'.", nameof(recheck));

        var provenance = decision.Prediction.Provenance;
        if (provenance.Sample is not { Drawn: true } sample)
            throw new ArgumentException(
                $"Decision '{decision.DecisionId}' was not drawn for recheck; only drawn decisions are weighted 1/π.", nameof(decision));

        var verdict = recheck.Verdict;
        var outcome = !verdict.Answered
            ? RecheckOutcome.UpperFailed
            : policy.Agrees(decision.Prediction.Value, verdict.Value!) ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed;

        return new RecheckRecord
        {
            DecisionId = decision.DecisionId,
            TierId = provenance.TierId ?? throw new ArgumentException("The decision's provenance names no tier.", nameof(decision)),
            SliceKey = provenance.SliceKey ?? "",
            DecidedAt = decision.DecidedAt,
            Probability = sample.Probability,
            Knob = sample.Knob,
            KnobRates = sample.KnobRates,
            SnapshotVersion = provenance.SnapshotVersion,
            Outcome = outcome,
            Confidence = provenance.Confidence,
            Risk = sample.Signal?.Risk,
            Weight = sample.Signal?.Weight,
            UpperMustCatch = verdict.Answered && policy.MustCatch(verdict.Value!),
            CheckedBy = recheck.Tier.Id,
        };
    }

    /// <summary>
    /// The tiers below the settling one that answered and sent the decision up, when the answer it
    /// was settled with is a must-catch value — each one's "caught" in its ledger cell. Fully
    /// observed: every escalated decision is judged above.
    /// </summary>
    public static IReadOnlyList<string> CaughtBy<TValue>(RoutedDecision<TValue> decision, ISettlementPolicy<TValue> policy)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.MustCatch(decision.Prediction.Value))
            return [];
        return decision.Steps
            .Where(s => s.Outcome == StepOutcome.Escalated)
            .Select(s => s.Tier.Id)
            .ToList();
    }
}
