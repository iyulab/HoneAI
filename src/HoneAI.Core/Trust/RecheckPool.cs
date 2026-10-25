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
/// <param name="Reached">Of those, the ones a judge answered or passed.</param>
public sealed record PoolReach(string TierId, string SliceKey, int Pooled, int Reached)
{
    /// <summary>The share reached, r — what each reached decision's π is multiplied by.</summary>
    public double Rate => Pooled == 0 ? 0 : (double)Reached / Pooled;
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
    /// Up to <paramref name="count"/> items to show <paramref name="reviewerKey"/> now, holding each for them until
    /// <paramref name="now"/> + <paramref name="hold"/> — in an order fixed by the decision and
    /// <paramref name="sessionKey"/>, never by the item. Items already answered, expired, or held by someone else are
    /// not offered; the reviewer's own unexpired holds come back first.
    /// </summary>
    /// <param name="store">The pool.</param>
    /// <param name="reviewerKey">Who is judging — a pseudonymous key.</param>
    /// <param name="sessionKey">This session's key — a new one deals a new order.</param>
    /// <param name="count">How many to offer.</param>
    /// <param name="now">Now.</param>
    /// <param name="hold">How long an offered item stays theirs.</param>
    /// <param name="tierId">Only items of this measured tier; every tier when null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<PoolPresentation>> PresentAsync(
        IRecheckPoolStore store, string reviewerKey, string sessionKey, int count, DateTimeOffset now, TimeSpan hold,
        string? tierId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (hold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(hold), hold, "A hold lasts some time.");

        var open = new List<(PoolEntry Entry, bool Own)>();
        await foreach (var entry in store.ReadAsync(DateTimeOffset.MinValue, now, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Response is not null || entry.Item.ExpiresAt <= now || (tierId is not null && entry.Item.TierId != tierId))
                continue;
            var held = entry.Lease is { } lease && lease.Until > now;
            var own = held && entry.Lease!.ReviewerKey == reviewerKey;
            if (!held || own)
                open.Add((entry, own));
        }

        var chosen = open
            .OrderByDescending(x => x.Own)
            .ThenBy(x => Order(x.Entry.Item, sessionKey))
            .Take(count)
            .Select(x => x.Entry.Item)
            .ToList();
        foreach (var item in chosen)
            await store.LeaseAsync(new PoolLease(item.DecisionId, item.TierId, reviewerKey, now + hold), cancellationToken).ConfigureAwait(false);
        return chosen.Select(i => new PoolPresentation(i.DecisionId, i.TierId, i.SliceKey, i.InputRef)).ToList();
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
        var reach = entries
            .GroupBy(e => (e.Item.TierId, e.Item.SliceKey))
            .OrderBy(g => g.Key.TierId, StringComparer.Ordinal).ThenBy(g => g.Key.SliceKey, StringComparer.Ordinal)
            .Select(g => new PoolReach(g.Key.TierId, g.Key.SliceKey, g.Count(), g.Count(e => e.Response is not null)))
            .ToList();
        if (entries.Any(e => e.Response is null && e.Item.ExpiresAt > now))
            return new PoolAccount(false, [], reach);

        var rate = reach.ToDictionary(r => (r.TierId, r.SliceKey), r => r.Rate);
        var records = entries
            .Where(e => e.Response is not null)
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

    // A number fixed by the decision and the session: an order nothing about the item can sway.
    private static ulong Order(PoolItem item, string sessionKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey + "\n" + item.TierId + "\n" + item.DecisionId), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
