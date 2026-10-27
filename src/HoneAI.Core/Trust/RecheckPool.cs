using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Trust;

/// <summary>The reach of a tier in a slice over a window: how many of its pooled decisions a judge reached.</summary>
/// <param name="TierId">The tier measured.</param>
/// <param name="SliceKey">The slice.</param>
/// <param name="Pooled">Decisions pooled.</param>
/// <param name="Reached">Of those, the ones a judge answered, passed or could not tell.</param>
/// <param name="Unsure">Of the reached, the ones the judge could not tell (<see cref="PoolResponseKind.Unsure"/>) — in <paramref name="Reached"/>, but with no record in the account.</param>
public sealed record PoolReach(string TierId, string SliceKey, int Pooled, int Reached, int Unsure = 0)
{
    /// <summary>The share reached, r — what each reached decision's π is multiplied by.</summary>
    public double Rate => Pooled == 0 ? 0 : (double)Reached / Pooled;
}

/// <summary>
/// What a judging session mixes in besides rechecks — shares of the session, together under 1 — so planted items,
/// repeats and second opinions reach the judge among the rechecks, looking the same.
/// </summary>
public sealed record SessionMix
{
    /// <summary>Rechecks only.</summary>
    public static SessionMix RechecksOnly { get; } = new();

    /// <summary>The share of planted items — the judge's own measurement.</summary>
    public double PlantShare { get; init; }

    /// <summary>The share of repeats — decisions this judge answered at least <see cref="MinimumRepeatGap"/> ago.</summary>
    public double RepeatShare { get; init; }

    /// <summary>The share of second opinions — decisions another judge answered.</summary>
    public double SecondOpinionShare { get; init; }

    /// <summary>How long before an answered decision comes back to the same judge as a repeat — long enough not to be remembered.</summary>
    public TimeSpan MinimumRepeatGap { get; init; } = TimeSpan.FromDays(7);

    internal IEnumerable<(PoolPurpose Purpose, double Share)> Shares =>
        [(PoolPurpose.Plant, PlantShare), (PoolPurpose.Repeat, RepeatShare), (PoolPurpose.SecondOpinion, SecondOpinionShare)];

    internal void Validate()
    {
        if (Shares.Any(s => !(s.Share >= 0)) || !(PlantShare + RepeatShare + SecondOpinionShare < 1))
            throw new InvalidOperationException("Mixed-in shares are not negative and together under 1 — a session is mostly rechecks.");
        if (MinimumRepeatGap < TimeSpan.Zero)
            throw new InvalidOperationException("MinimumRepeatGap is not negative.");
    }
}

/// <summary>What a closed window of the pool gives the ledger.</summary>
/// <param name="Closed">
/// Every item decided in the window has been answered, passed or has expired. Until then the reach is not known and
/// nothing is given: an item still waiting could yet be reached.
/// </param>
/// <param name="Records">The rechecks, one per reached item, at π × r.</param>
/// <param name="Reach">The reach per tier and slice.</param>
public sealed record PoolAccount(bool Closed, IReadOnlyList<RecheckRecord> Records, IReadOnlyList<PoolReach> Reach);

/// <summary>
/// The recheck pool for a judge that comes when it comes: drawn decisions wait in it (<see cref="IRecheckPoolStore"/>),
/// a judge is shown them in an order that has nothing to do with the decisions, and when a window closes the reached
/// ones become rechecks for the ledger.
/// </summary>
/// <remarks>
/// <para>
/// What makes an unreached item ignorable is the order: it is fixed by a hash of the decision id and the session, so
/// whether an item is reached depends on how much the judge had time for, not on the item. The reached items are then
/// a random thinning of the pooled ones, at the rate r the window shows — each counts at π × r, and an unreached or
/// expired item is left out rather than counted as a failure. An item the judge saw and passed is a failure of the
/// tier above, counted at both ends of an interval as an abstention is.
/// </para>
/// <para>
/// The reach is measured per tier and slice over the window. Within it the assumption is that reach does not depend on
/// the item — what the hashed order buys — and not on when in the window the decision came; a consumer whose judges
/// come in bursts sets windows short enough that a burst covers them evenly.
/// </para>
/// </remarks>
public static class RecheckPool
{
    /// <summary>
    /// Up to <paramref name="count"/> items to show <paramref name="reviewerKey"/> now — rechecks, with planted items,
    /// repeats and second opinions mixed in at the shares of <paramref name="mix"/> — in an order fixed by the decision,
    /// the purpose and <paramref name="sessionKey"/>, never by the item. A recheck is held for the reviewer until
    /// <paramref name="now"/> + <paramref name="hold"/>; one already answered, expired, or held by someone else is not
    /// offered, and the reviewer's own unexpired holds are offered before anyone else's free ones. A share with nothing to offer is filled with rechecks.
    /// </summary>
    /// <param name="store">The pool.</param>
    /// <param name="reviewerKey">Who is judging — a pseudonymous key.</param>
    /// <param name="sessionKey">This session's key — a new one deals a new order.</param>
    /// <param name="count">How many to offer.</param>
    /// <param name="now">Now.</param>
    /// <param name="hold">How long an offered recheck stays theirs.</param>
    /// <param name="tierId">Only items of this measured tier; every tier when null.</param>
    /// <param name="mix">What else to mix in; rechecks only when null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<PoolPresentation>> PresentAsync(
        IRecheckPoolStore store, string reviewerKey, string sessionKey, int count, DateTimeOffset now, TimeSpan hold,
        string? tierId = null, SessionMix? mix = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (hold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(hold), hold, "A hold lasts some time.");
        mix ??= SessionMix.RechecksOnly;
        mix.Validate();

        var rechecks = new List<(PoolItem Item, bool Own)>();
        var candidates = new Dictionary<PoolPurpose, List<PoolItem>>
        {
            [PoolPurpose.Plant] = [],
            [PoolPurpose.Repeat] = [],
            [PoolPurpose.SecondOpinion] = [],
        };
        await foreach (var entry in store.ReadAsync(DateTimeOffset.MinValue, now, cancellationToken).ConfigureAwait(false))
        {
            var item = entry.Item;
            if (tierId is not null && item.TierId != tierId)
                continue;
            bool AnsweredBy(PoolPurpose purpose) => entry.ResponsesOf(purpose).Any(r => r.ReviewerKey == reviewerKey);

            if (item.Kind == PoolItemKind.Plant)
            {
                if (item.ExpiresAt > now && !AnsweredBy(PoolPurpose.Plant))
                    candidates[PoolPurpose.Plant].Add(item);
                continue;
            }
            if (entry.Response is { } first)
            {
                // Answered: a repeat for the one who answered (after a while, so it is not remembered), a second
                // opinion for anyone else — each once per judge.
                if (first.Kind == PoolResponseKind.Answered && first.ReviewerKey == reviewerKey)
                {
                    if (now - first.RespondedAt >= mix.MinimumRepeatGap && !AnsweredBy(PoolPurpose.Repeat))
                        candidates[PoolPurpose.Repeat].Add(item);
                }
                else if (first.Kind == PoolResponseKind.Answered && first.ReviewerKey is not null && !AnsweredBy(PoolPurpose.SecondOpinion))
                {
                    candidates[PoolPurpose.SecondOpinion].Add(item);
                }
                continue;
            }
            if (item.ExpiresAt <= now)
                continue;
            var held = entry.Lease is { } lease && lease.Until > now;
            var own = held && entry.Lease!.ReviewerKey == reviewerKey;
            if (!held || own)
                rechecks.Add((item, own));
        }

        var chosen = new List<(PoolItem Item, PoolPurpose Purpose)>();
        foreach (var (purpose, share) in mix.Shares)
        {
            chosen.AddRange(candidates[purpose]
                .OrderBy(i => Order(i, purpose, sessionKey))
                .Take((int)Math.Round(count * share, MidpointRounding.AwayFromZero))
                .Select(i => (i, purpose)));
        }
        chosen.AddRange(rechecks
            .OrderByDescending(x => x.Own)
            .ThenBy(x => Order(x.Item, PoolPurpose.Recheck, sessionKey))
            .Take(Math.Max(0, count - chosen.Count))
            .Select(x => (x.Item, PoolPurpose.Recheck)));

        foreach (var (item, purpose) in chosen.Where(c => c.Purpose == PoolPurpose.Recheck))
            await store.LeaseAsync(new PoolLease(item.DecisionId, item.TierId, reviewerKey, now + hold), cancellationToken).ConfigureAwait(false);
        return chosen
            .OrderBy(c => Order(c.Item, c.Purpose, sessionKey))
            .Take(count)
            .Select(c => new PoolPresentation(c.Item.DecisionId, c.Item.TierId, c.Item.SliceKey, c.Item.InputRef, c.Purpose))
            .ToList();
    }

    /// <summary>
    /// The rechecks the window [<paramref name="from"/>, <paramref name="to"/>) of decision time gives the ledger, once
    /// it is closed at <paramref name="now"/>.
    /// </summary>
    public static async Task<PoolAccount> AccountAsync(
        IRecheckPoolStore store, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var entries = new List<PoolEntry>();
        await foreach (var entry in store.ReadAsync(from, to, cancellationToken).ConfigureAwait(false))
            entries.Add(entry);
        return Account(entries, now);
    }

    /// <summary>The account of <paramref name="entries"/> — one window's — at <paramref name="now"/>.</summary>
    public static PoolAccount Account(IReadOnlyCollection<PoolEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        entries = entries.Where(e => e.Item.Kind == PoolItemKind.Recheck).ToList();
        var reach = entries
            .GroupBy(e => (e.Item.TierId, e.Item.SliceKey))
            .OrderBy(g => g.Key.TierId, StringComparer.Ordinal).ThenBy(g => g.Key.SliceKey, StringComparer.Ordinal)
            .Select(g => new PoolReach(g.Key.TierId, g.Key.SliceKey, g.Count(), g.Count(e => e.Response is not null),
                g.Count(e => e.Response?.Kind == PoolResponseKind.Unsure)))
            .ToList();
        if (entries.Any(e => e.Response is null && e.Item.ExpiresAt > now))
            return new PoolAccount(false, [], reach);

        var rate = reach.ToDictionary(r => (r.TierId, r.SliceKey), r => r.Rate);
        var records = entries
            .Where(e => e.Response is not null && e.Response.Kind != PoolResponseKind.Unsure)
            .OrderBy(e => e.Item.DecidedAt)
            .Select(e => Record(e.Item, e.Response!, rate[(e.Item.TierId, e.Item.SliceKey)]))
            .ToList();
        return new PoolAccount(true, records, reach);
    }

    private static RecheckRecord Record(PoolItem item, PoolResponse response, double reachRate) => new()
    {
        DecisionId = item.DecisionId,
        TierId = item.TierId,
        SliceKey = item.SliceKey,
        DecidedAt = item.DecidedAt,
        Probability = item.Probability * reachRate,
        Knob = item.Knob,
        KnobRates = item.KnobRates,
        SnapshotVersion = item.SnapshotVersion,
        Outcome = response.Kind == PoolResponseKind.Skipped
            ? RecheckOutcome.UpperFailed
            : response.Outcome ?? throw new InvalidOperationException(
                $"The answer to '{item.DecisionId}' says neither agreed nor disagreed."),
        UpperMustCatch = response.Kind == PoolResponseKind.Answered && response.UpperMustCatch,
        Confidence = item.Confidence,
        Risk = item.Risk,
        Weight = item.Weight,
        CheckedBy = response.CheckedBy,
        Blind = true,
    };

    // A number fixed by the decision, the purpose and the session: an order nothing about the item can sway.
    private static ulong Order(PoolItem item, PoolPurpose purpose, string sessionKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey + "\n" + purpose + "\n" + item.TierId + "\n" + item.DecisionId), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
