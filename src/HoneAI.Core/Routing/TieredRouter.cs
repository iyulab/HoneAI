using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Routing;

/// <summary>
/// Routes a decision up a ladder of tiers, cheapest first. A tier below the top settles a decision
/// only when it has earned that in the input's slice — its <see cref="TrustState"/> in the current
/// <see cref="PolicySnapshot"/> is <see cref="TrustState.Active"/> or <see cref="TrustState.Stable"/> —
/// and only with an answer the <see cref="ISettlementPolicy{TValue}"/> lets it settle. Confidence is
/// not a licence: a judge uses it, if at all, to abstain. A decision that goes up carries no lower
/// answer; the top tier settles whatever it answers.
/// </summary>
/// <remarks>
/// Every decision a lower tier settles is drawn for a recheck with probability π, deterministically —
/// <see cref="RecheckDraw"/> over the decision id and snapshot version — and the draw is recorded in
/// the provenance before anything above sees the decision, so a later count can weight it by 1/π.
/// </remarks>
/// <typeparam name="TInput">What is judged.</typeparam>
/// <typeparam name="TValue">The answer.</typeparam>
public sealed class TieredRouter<TInput, TValue> : IReasoningRouter<TInput, TValue>
{
    private readonly IJudge<TInput, TValue>[] _judges;
    private readonly ISettlementPolicy<TValue> _policy;
    private readonly ISliceKey<TInput> _sliceKey;
    private readonly Func<TInput, string> _decisionIdOf;
    private readonly Func<PolicySnapshot?> _snapshot;
    private readonly TieredRouterOptions _options;
    private (PolicySnapshot Snapshot, Dictionary<(string, string), PolicyEntry> Entries)? _index;

    /// <param name="judges">The tiers. Ordered by <see cref="Tier.CostRank"/>; ids and ranks must be distinct.</param>
    /// <param name="policy">Which answers a tier may settle and which must go to the top.</param>
    /// <param name="sliceKey">How an input maps to a slice.</param>
    /// <param name="decisionIdOf">The decision's identifier — stable across retries of the same input.</param>
    /// <param name="snapshot">The policy in force; <see langword="null"/> means none yet (every slice in shadow).</param>
    /// <param name="options">Timeouts, the recheck floor, the clock.</param>
    public TieredRouter(
        IEnumerable<IJudge<TInput, TValue>> judges,
        ISettlementPolicy<TValue> policy,
        ISliceKey<TInput> sliceKey,
        Func<TInput, string> decisionIdOf,
        Func<PolicySnapshot?> snapshot,
        TieredRouterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(judges);
        _judges = judges.OrderBy(j => j.Tier.CostRank).ToArray();
        if (_judges.Length == 0)
            throw new ArgumentException("At least one judge is needed.", nameof(judges));
        if (_judges.Select(j => j.Tier.Id).Distinct(StringComparer.Ordinal).Count() != _judges.Length)
            throw new ArgumentException("Tier ids must be distinct.", nameof(judges));
        if (_judges.Select(j => j.Tier.CostRank).Distinct().Count() != _judges.Length)
            throw new ArgumentException("Tier cost ranks must be distinct — the ladder has one order.", nameof(judges));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sliceKey = sliceKey ?? throw new ArgumentNullException(nameof(sliceKey));
        _decisionIdOf = decisionIdOf ?? throw new ArgumentNullException(nameof(decisionIdOf));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _options = options ?? new TieredRouterOptions();
        if (!(_options.MinimumSampleRate is > 0 and <= 1))
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumSampleRate must be in (0, 1] — π never reaches 0.");
    }

    /// <summary>The tiers, lowest first.</summary>
    public IReadOnlyList<Tier> Tiers => _judges.Select(j => j.Tier).ToArray();

    /// <inheritdoc />
    public async Task<ITracedPrediction<TValue>> RouteAsync(TInput query, CancellationToken cancellationToken = default)
        => (await DecideAsync(query, cancellationToken).ConfigureAwait(false)).Prediction;

    /// <summary>
    /// Route <paramref name="input"/> and return the decision with every tier's verdict — including
    /// what a tier in shadow would have answered, which the provenance (not generic) cannot carry.
    /// </summary>
    /// <exception cref="DecisionNotSettledException">No tier settled the decision.</exception>
    public async Task<RoutedDecision<TValue>> DecideAsync(TInput input, CancellationToken cancellationToken = default)
    {
        var decisionId = _decisionIdOf(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId, nameof(decisionId));
        var slice = _sliceKey.KeyOf(input);
        var snapshot = _snapshot();
        var now = _options.Clock();
        var hardExpired = snapshot is not null && now >= snapshot.HardExpiresAt;
        // Trust earned under another slice-key definition is about other slices: none of it applies.
        var entriesApply = snapshot is not null
            && (snapshot.SliceKeyVersion is null || snapshot.SliceKeyVersion == _sliceKey.Version);

        var steps = new List<RoutedStep<TValue>>(_judges.Length);
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        RoutedStep<TValue>? candidate = null; // the highest answer held back by an expired policy

        for (var i = 0; i < _judges.Length; i++)
        {
            var judge = _judges[i];
            var tier = judge.Tier;
            var isTop = i == _judges.Length - 1;
            var entry = entriesApply ? Find(snapshot!, tier.Id, slice) : null;
            var passHints = tier.AcceptsHints && hints.Count > 0;

            var context = new JudgeContext
            {
                DecisionId = decisionId,
                SliceKey = slice,
                AbstainQuantile = entry?.AbstainQuantile,
                Hints = passHints ? new Dictionary<string, string>(hints, StringComparer.Ordinal) : null,
            };

            var verdict = await JudgeAsync(judge, input, context, cancellationToken).ConfigureAwait(false);
            if (verdict.Hints is { } offered)
                foreach (var (k, v) in offered)
                    hints[k] = v;

            var state = entry?.State ?? TrustState.Shadow;
            if (!verdict.Answered)
            {
                steps.Add(new RoutedStep<TValue>(tier, verdict, StepOutcome.Abstained, verdict.Rationale, state, passHints));
                continue;
            }

            var value = verdict.Value!;
            if (isTop)
            {
                var top = new RoutedStep<TValue>(tier, verdict, StepOutcome.Settled, null, entry?.State, passHints);
                steps.Add(top);
                return Settle(decisionId, slice, snapshot, steps, top, sample: null, unverified: false);
            }

            string? held = _policy.MustCatch(value) ? "must-catch value"
                : !_policy.MaySettle(tier, value) ? "not settleable by this tier"
                : null;
            if (held is not null)
            {
                steps.Add(new RoutedStep<TValue>(tier, verdict, StepOutcome.Escalated, held, state, passHints));
                continue;
            }

            if (state == TrustState.Shadow)
            {
                steps.Add(new RoutedStep<TValue>(tier, verdict, StepOutcome.Shadowed,
                    entriesApply || snapshot is null ? "in shadow for this slice" : "snapshot slice-key version differs", state, passHints));
                continue;
            }

            if (hardExpired)
            {
                // Fail closed: the policy that vouched for this tier has expired. Go up; if nothing
                // above can answer, this answer is used — marked unverified.
                var step = new RoutedStep<TValue>(tier, verdict, StepOutcome.Escalated, "policy snapshot expired", state, passHints);
                steps.Add(step);
                candidate = step; // a higher tier's held answer replaces a lower one's
                continue;
            }

            var settled = new RoutedStep<TValue>(tier, verdict, StepOutcome.Settled, null, state, passHints);
            steps.Add(settled);
            return Settle(decisionId, slice, snapshot, steps, settled, Draw(decisionId, snapshot!, entry!), unverified: false);
        }

        if (candidate is not null)
        {
            // Nothing above could answer. Settle with the held answer, as unverified, still drawn for a recheck.
            var index = steps.IndexOf(candidate);
            var settled = candidate with { Outcome = StepOutcome.Settled, Detail = "policy snapshot expired; no tier above answered" };
            steps[index] = settled;
            var entry = Find(snapshot!, candidate.Tier.Id, slice);
            return Settle(decisionId, slice, snapshot, steps, settled, Draw(decisionId, snapshot!, entry!), unverified: true);
        }

        throw new DecisionNotSettledException(decisionId, steps.Select(ToTierStep).ToArray());
    }

    private RoutedDecision<TValue> Settle(
        string decisionId, string slice, PolicySnapshot? snapshot, List<RoutedStep<TValue>> steps,
        RoutedStep<TValue> settled, SampleDraw? sample, bool unverified)
    {
        var provenance = new PredictionProvenance
        {
            Kind = settled.Tier.Kind,
            TierId = settled.Tier.Id,
            TierVersion = settled.Tier.Version,
            Confidence = settled.Verdict.Confidence,
            Rationale = settled.Verdict.Rationale,
            SliceKey = slice,
            TrustState = settled.State,
            SnapshotVersion = snapshot?.Version,
            Path = steps.Select(ToTierStep).ToArray(),
            Sample = sample,
            HadHint = settled.HadHint,
            Unverified = unverified,
        };
        return new RoutedDecision<TValue>(
            decisionId, new TracedPrediction<TValue>(settled.Verdict.Value!, provenance), steps.ToArray());
    }

    private SampleDraw Draw(string decisionId, PolicySnapshot snapshot, PolicyEntry entry)
    {
        var (knob, rate) = entry.SampleRates is { Count: > 0 } rates
            ? rates.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => (r.Key, r.Value)).First()
            : ("none", 0.0);
        if (!(rate >= _options.MinimumSampleRate))
            (knob, rate) = ("floor", _options.MinimumSampleRate);
        rate = Math.Min(rate, 1.0);
        return new SampleDraw(rate, knob, RecheckDraw.IsDrawn(decisionId, snapshot.Version, rate));
    }

    private async ValueTask<JudgeVerdict<TValue>> JudgeAsync(
        IJudge<TInput, TValue> judge, TInput input, JudgeContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.JudgeTimeout is { } limit)
            timeout.CancelAfter(limit);
        try
        {
            return await judge.JudgeAsync(input, context, timeout.Token).ConfigureAwait(false)
                ?? JudgeVerdict<TValue>.Abstain("judge returned no verdict");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return JudgeVerdict<TValue>.Abstain("timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JudgeVerdict<TValue>.Abstain($"error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private PolicyEntry? Find(PolicySnapshot snapshot, string tierId, string slice)
    {
        var index = _index;
        if (index is null || !ReferenceEquals(index.Value.Snapshot, snapshot))
        {
            var entries = new Dictionary<(string, string), PolicyEntry>();
            foreach (var e in snapshot.Entries)
                entries[(e.TierId, e.SliceKey)] = e;
            index = (snapshot, entries);
            _index = index;
        }
        return index.Value.Entries.GetValueOrDefault((tierId, slice));
    }

    private static TierStep ToTierStep(RoutedStep<TValue> s) => new(s.Tier.Id, s.Tier.Kind, s.Outcome, s.Detail ?? (s.Outcome == StepOutcome.Abstained ? s.Verdict.Rationale : null));
}

/// <summary>Options for <see cref="TieredRouter{TInput,TValue}"/>.</summary>
public sealed record TieredRouterOptions
{
    /// <summary>How long a judge may take before it counts as having abstained. None by default.</summary>
    public TimeSpan? JudgeTimeout { get; init; }

    /// <summary>
    /// The least recheck probability a settled decision is drawn with, whatever the snapshot says —
    /// π never reaches 0, or the first wrong answer in a quiet slice is never seen. Default 0.01.
    /// </summary>
    public double MinimumSampleRate { get; init; } = 0.01;

    /// <summary>Time source for snapshot expiry. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>A routed decision with every tier's verdict.</summary>
/// <param name="DecisionId">The decision.</param>
/// <param name="Prediction">The settled value and its provenance.</param>
/// <param name="Steps">Each tier's verdict and what became of it, lowest first.</param>
public sealed record RoutedDecision<TValue>(
    string DecisionId, ITracedPrediction<TValue> Prediction, IReadOnlyList<RoutedStep<TValue>> Steps);

/// <summary>One tier's verdict on a decision.</summary>
/// <param name="Tier">The tier.</param>
/// <param name="Verdict">What it answered, or why it abstained.</param>
/// <param name="Outcome">What became of the verdict.</param>
/// <param name="Detail">Why it did not settle, when it did not.</param>
/// <param name="State">The tier's state in the slice; <see langword="null"/> for the top tier without an entry.</param>
/// <param name="HadHint">Whether hints were passed to it.</param>
public sealed record RoutedStep<TValue>(
    Tier Tier, JudgeVerdict<TValue> Verdict, StepOutcome Outcome, string? Detail, TrustState? State, bool HadHint);

/// <summary>No tier settled a decision — every tier abstained, or held its answer back.</summary>
public sealed class DecisionNotSettledException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DecisionNotSettledException(string decisionId, IReadOnlyList<TierStep> path)
        : base($"No tier settled decision '{decisionId}': "
               + string.Join("; ", path.Select(p => $"{p.TierId} {p.Outcome}{(p.Detail is null ? "" : $" ({p.Detail})")}")))
    {
        DecisionId = decisionId;
        Path = path;
    }

    /// <summary>The decision.</summary>
    public string DecisionId { get; }

    /// <summary>What each tier did.</summary>
    public IReadOnlyList<TierStep> Path { get; }
}
