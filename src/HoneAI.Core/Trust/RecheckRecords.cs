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
    /// <exception cref="ArgumentException">The decision was not drawn for recheck, the recheck is of another decision, or it was shown the lower answer.</exception>
    public static RecheckRecord From<TValue>(
        RoutedDecision<TValue> decision, RecheckResult<TValue> recheck, ISettlementPolicy<TValue> policy)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(recheck);
        ArgumentNullException.ThrowIfNull(policy);
        if (recheck.Shown)
            throw new ArgumentException(
                "The recheck was shown the lower answer; only a blind recheck is counted in the ledger.", nameof(recheck));
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

    /// <summary>The knob a shadow tier's records are drawn under: every shadowed decision is checked, so π = 1.</summary>
    public const string ShadowKnob = "shadow";

    /// <summary>
    /// The records a settled decision gives the tiers that were in shadow for its slice: a shadow tier answered
    /// something it would have settled, and the tier that did settle the decision answered it too — so the one is
    /// checked against the other at no more cost, with probability 1. Agreement and a must-catch value are the
    /// settlement policy's, counted as a live recheck counts them (the shadow tier's answer is what is checked; the
    /// settling tier's is the check), so a shadow window fills the same ledger cell a stable tier's rechecks do and the
    /// shadow → active release can be judged on it. A decision settled while a policy snapshot was expired is not a
    /// check — nothing vouched for the settling answer — and gives none.
    /// </summary>
    public static IReadOnlyList<RecheckRecord> FromShadow<TValue>(RoutedDecision<TValue> decision, ISettlementPolicy<TValue> policy)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(policy);
        var provenance = decision.Prediction.Provenance;
        if (provenance.Unverified)
            return [];
        var settled = decision.Steps.LastOrDefault(s => s.Outcome == StepOutcome.Settled);
        if (settled is null || !settled.Verdict.Answered)
            return [];

        var records = new List<RecheckRecord>();
        foreach (var step in decision.Steps)
        {
            if (step.Outcome != StepOutcome.Shadowed || !step.Verdict.Answered)
                continue;
            records.Add(new RecheckRecord
            {
                DecisionId = decision.DecisionId,
                TierId = step.Tier.Id,
                SliceKey = provenance.SliceKey ?? "",
                DecidedAt = decision.DecidedAt,
                Probability = 1.0,
                Knob = ShadowKnob,
                SnapshotVersion = provenance.SnapshotVersion,
                Outcome = policy.Agrees(step.Verdict.Value!, settled.Verdict.Value!) ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed,
                Confidence = step.Verdict.Confidence,
                UpperMustCatch = policy.MustCatch(settled.Verdict.Value!),
                CheckedBy = settled.Tier.Id,
            });
        }
        return records;
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
