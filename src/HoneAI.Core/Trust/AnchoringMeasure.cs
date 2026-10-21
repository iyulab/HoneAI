using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Routing;

namespace HoneAI.Trust;

/// <summary>
/// One decision a teacher judged twice — blind, and shown the lower answer. Whether it agreed with
/// the lower answer each time, and, when the answer is known, whether it was right each time.
/// Null where the teacher gave no answer.
/// </summary>
public sealed record PairRecord
{
    public required string DecisionId { get; init; }

    /// <summary>The teacher.</summary>
    public required string TierId { get; init; }

    public required string SliceKey { get; init; }

    /// <summary>When the later of the two judgments was made.</summary>
    public required DateTimeOffset JudgedAt { get; init; }

    /// <summary>Blind, the teacher agreed with the lower answer.</summary>
    public bool? AgreedBlind { get; init; }

    /// <summary>Shown the lower answer, the teacher agreed with it.</summary>
    public bool? AgreedShown { get; init; }

    /// <summary>Blind, the teacher was right — only when the answer is known.</summary>
    public bool? CorrectBlind { get; init; }

    /// <summary>Shown, the teacher was right — only when the answer is known.</summary>
    public bool? CorrectShown { get; init; }
}

/// <summary>What showing the lower answer did to the teacher, over a set of pairs.</summary>
public sealed record AnchoringCell
{
    public required string TierId { get; init; }

    /// <summary>Pairs where the teacher answered both times.</summary>
    public required int Pairs { get; init; }

    /// <summary>Agreement with the lower answer, blind. Null without a pair.</summary>
    public double? AgreementBlind { get; init; }

    /// <summary>Agreement with the lower answer, shown. Null without a pair.</summary>
    public double? AgreementShown { get; init; }

    /// <summary>Pairs that disagreed blind and agreed once shown — the teacher moved to the lower answer.</summary>
    public required int MovedToLower { get; init; }

    /// <summary>Pairs that agreed blind and disagreed once shown — the teacher moved away from it.</summary>
    public required int MovedAway { get; init; }

    /// <summary>
    /// Exact two-sided McNemar p-value on the moves: how likely moves this lopsided are if showing
    /// changes nothing. Null with no move.
    /// </summary>
    public double? AgreementShiftP { get; init; }

    /// <summary>Pairs with a known answer where the teacher answered both times.</summary>
    public required int KnownPairs { get; init; }

    /// <summary>Accuracy blind, on the known pairs. Null without one.</summary>
    public double? AccuracyBlind { get; init; }

    /// <summary>Accuracy shown, on the known pairs. Null without one.</summary>
    public double? AccuracyShown { get; init; }

    /// <summary>Exact two-sided McNemar p-value on the accuracy moves. Null with no move.</summary>
    public double? AccuracyShiftP { get; init; }
}

/// <summary>
/// Measures anchoring: whether a teacher shown the lower answer agrees with it more than it does
/// blind, and whether that makes it more or less right. Blindness is the default on the recheck path
/// because the measurement literature makes it so, not because it was measured here — this is how
/// it gets measured.
/// </summary>
public static class AnchoringMeasure
{
    /// <summary>
    /// The pair from a blind and a shown judgment of one decision whose lower answer was
    /// <paramref name="lowerAnswer"/>. With <paramref name="known"/>, each judgment is also scored
    /// against the known answer.
    /// </summary>
    /// <exception cref="ArgumentException">The two are of different decisions or teachers, or the "blind" one was shown, or the "shown" one was not.</exception>
    public static PairRecord Pair<TValue>(
        TValue lowerAnswer, RecheckResult<TValue> blind, RecheckResult<TValue> shown, string sliceKey,
        ISettlementPolicy<TValue> policy, Known<TValue>? known = null)
    {
        ArgumentNullException.ThrowIfNull(blind);
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sliceKey);
        if (blind.DecisionId != shown.DecisionId || blind.Tier.Id != shown.Tier.Id)
            throw new ArgumentException("A pair is one decision judged twice by one teacher.", nameof(shown));
        if (blind.Shown || !shown.Shown)
            throw new ArgumentException("A pair is one blind judgment and one shown judgment.", nameof(blind));

        bool? Agreed(RecheckResult<TValue> r) => r.Verdict.Answered ? policy.Agrees(lowerAnswer, r.Verdict.Value!) : null;
        bool? Correct(RecheckResult<TValue> r) => known is { } k && r.Verdict.Answered ? policy.Agrees(k.Value, r.Verdict.Value!) : null;

        return new PairRecord
        {
            DecisionId = blind.DecisionId,
            TierId = blind.Tier.Id,
            SliceKey = sliceKey,
            JudgedAt = blind.JudgedAt > shown.JudgedAt ? blind.JudgedAt : shown.JudgedAt,
            AgreedBlind = Agreed(blind),
            AgreedShown = Agreed(shown),
            CorrectBlind = Correct(blind),
            CorrectShown = Correct(shown),
        };
    }

    /// <summary>The cell for <paramref name="tierId"/> over <paramref name="pairs"/>.</summary>
    public static AnchoringCell Compute(IEnumerable<PairRecord> pairs, string tierId)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var mine = pairs.Where(p => p.TierId == tierId).ToList();

        var answered = mine.Where(p => p.AgreedBlind is not null && p.AgreedShown is not null).ToList();
        var toLower = answered.Count(p => p.AgreedBlind == false && p.AgreedShown == true);
        var away = answered.Count(p => p.AgreedBlind == true && p.AgreedShown == false);

        var known = mine.Where(p => p.CorrectBlind is not null && p.CorrectShown is not null).ToList();
        var gained = known.Count(p => p.CorrectBlind == false && p.CorrectShown == true);
        var lost = known.Count(p => p.CorrectBlind == true && p.CorrectShown == false);

        return new AnchoringCell
        {
            TierId = tierId,
            Pairs = answered.Count,
            AgreementBlind = answered.Count > 0 ? answered.Count(p => p.AgreedBlind == true) / (double)answered.Count : null,
            AgreementShown = answered.Count > 0 ? answered.Count(p => p.AgreedShown == true) / (double)answered.Count : null,
            MovedToLower = toLower,
            MovedAway = away,
            AgreementShiftP = McNemarExactP(toLower, away),
            KnownPairs = known.Count,
            AccuracyBlind = known.Count > 0 ? known.Count(p => p.CorrectBlind == true) / (double)known.Count : null,
            AccuracyShown = known.Count > 0 ? known.Count(p => p.CorrectShown == true) / (double)known.Count : null,
            AccuracyShiftP = McNemarExactP(gained, lost),
        };
    }

    /// <summary>
    /// Exact two-sided McNemar p-value for <paramref name="b"/> and <paramref name="c"/> discordant
    /// pairs: twice the binomial tail of the smaller count at ½, capped at 1. Null with no discordant pair.
    /// </summary>
    public static double? McNemarExactP(int b, int c)
    {
        if (b < 0 || c < 0) throw new ArgumentOutOfRangeException(b < 0 ? nameof(b) : nameof(c));
        var n = b + c;
        if (n == 0)
            return null;
        return Math.Min(1, 2 * TrustLedger.BinomialCdf(Math.Min(b, c), n, 0.5));
    }
}

/// <summary>A known answer — wraps a value so "no known answer" is distinct from a default value.</summary>
public readonly record struct Known<TValue>(TValue Value);
