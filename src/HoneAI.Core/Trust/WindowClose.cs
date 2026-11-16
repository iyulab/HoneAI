using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI.Learning;

namespace HoneAI.Trust;

/// <summary>The floor a teacher must clear to teach the tier below. People set it; the driver only applies it.</summary>
public sealed record TeacherPolicy
{
    /// <summary>The must-catch recall a teacher's lower bound has to reach (<see cref="TeacherLedger.Qualifies(TeacherCell, double)"/>).</summary>
    public required double MinimumRecall { get; init; }

    /// <summary>
    /// The margin that keeps a teacher on the line from flipping with every window: a qualified teacher keeps its qualification
    /// until its bound is under <see cref="MinimumRecall"/> − margin. 0 is no margin.
    /// </summary>
    public double Margin { get; init; }

    /// <summary>The confidence of the recall bounds.</summary>
    public double Confidence { get; init; } = TrustLedger.DefaultConfidence;
}

/// <summary>
/// One tier in one slice as a window closes: what the caller knows that the recorded rechecks do not — the traffic it expects, the
/// tier above that teaches it, and the live signals it measured elsewhere.
/// </summary>
/// <param name="TierId">The tier.</param>
/// <param name="SliceKey">The slice.</param>
/// <param name="Pool">The pool of tier-above capacity its rechecks come from.</param>
/// <param name="Traffic">What the slice is expected to see in the next window.</param>
/// <param name="TierVersion">The tier's current version; a slice not yet seen starts in shadow on it.</param>
public sealed record WindowSlice(string TierId, string SliceKey, string Pool, SliceTraffic Traffic, string TierVersion)
{
    /// <summary>
    /// The tier above that teaches this one (its rechecks and plants are read by this id). Null when nothing teaches it — the
    /// tier is then judged as taught by a qualified teacher, which is the caller's statement that no teacher needs measuring.
    /// </summary>
    public string? TeacherTierId { get; init; }

    /// <summary>
    /// The teacher's current version. Given, only plants judged at that version count toward its qualification (a new version is
    /// not qualified on the old one's plants); null counts every version pooled.
    /// </summary>
    public string? TeacherVersion { get; init; }

    /// <summary>Must-catch decisions the tier sent up and the tier above confirmed in the window (<see cref="TrustLedger.Compute"/>).</summary>
    public int Caught { get; init; }

    /// <summary>The tier's inputs or answers have moved (<see cref="StalenessWatch.Drifted"/> on the consumer's drift reading).</summary>
    public bool Drifted { get; init; }

    /// <summary>A new tier version was shown no worse than the one it replaces (<see cref="LiveSignals.VersionProvenNoWorse"/>).</summary>
    public bool VersionProvenNoWorse { get; init; }

    /// <summary>A person's hold on the slice.</summary>
    public SliceFreeze? Freeze { get; init; }

    /// <summary>Rates of knobs other than the promise knob (<see cref="SliceWindow.OtherKnobRates"/>).</summary>
    public IReadOnlyDictionary<string, double>? OtherKnobRates { get; init; }

    /// <summary>How knobs tilt toward decisions worth checking (<see cref="SliceWindow.Selection"/>).</summary>
    public IReadOnlyDictionary<string, KnobSelection>? Selection { get; init; }

    /// <summary>The abstain quantile a person asked for (<see cref="SliceWindow.AbstainQuantile"/>).</summary>
    public double? AbstainQuantile { get; init; }

    /// <summary>The expected-loss threshold above which the tier abstains (<see cref="SliceWindow.AbstainExpectedLossAbove"/>).</summary>
    public double? AbstainExpectedLossAbove { get; init; }

    /// <summary>Whether the tier's confidence is calibrated (<see cref="SliceWindow.Calibrated"/>).</summary>
    public bool Calibrated { get; init; } = true;
}

/// <summary>A learning tier whose retraining is judged at the window's close, with what the driver cannot read from the ledger.</summary>
/// <param name="TierId">The learning tier — its slices' drift and demotions are what the retrain decision reads.</param>
/// <param name="Policy">When it retrains.</param>
/// <param name="Signals">What the caller measured: last training, a run in flight, new labels, held-out rows, gate refusals. The
/// driver adds the model's staleness and demotion from the window.</param>
public sealed record LearnerWindow(string TierId, RetrainPolicy Policy, RetrainSignals Signals);

/// <summary>Everything a window's close reads except the recorded evidence and the kept state. A clock is not one of them: <see cref="Now"/> is given.</summary>
public sealed record WindowCloseInput
{
    /// <summary>The snapshot version to issue.</summary>
    public required string Version { get; init; }

    /// <summary>The window's end.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>The window's length: the closed window is [<see cref="Now"/> − <see cref="Window"/>, <see cref="Now"/>), and the stretch before it, the one a teacher's change of version is looked for against, is the same length.</summary>
    public required TimeSpan Window { get; init; }

    /// <summary>Every tier in every slice that closes.</summary>
    public required IReadOnlyList<WindowSlice> Slices { get; init; }

    /// <summary>What the controller applies — all of it set by people.</summary>
    public required ControllerPolicy Policy { get; init; }

    /// <summary>The floor a teacher must clear.</summary>
    public required TeacherPolicy Teacher { get; init; }

    /// <summary>The learning tiers whose retraining is judged.</summary>
    public IReadOnlyList<LearnerWindow> Learners { get; init; } = [];

    /// <summary>The version of the slice key (<see cref="TrustController.Close"/>).</summary>
    public string? SliceKeyVersion { get; init; }

    /// <summary>The confidence of the ledger cells.</summary>
    public double Confidence { get; init; } = TrustLedger.DefaultConfidence;
}

/// <summary>The recorded evidence a window's close reads.</summary>
/// <param name="Rechecks">Recheck records of the closed window, any tier and slice (those outside a slice's cell are ignored).</param>
/// <param name="PreviousRechecks">Recheck records of the stretch before it.</param>
/// <param name="Plants">Plant records of the closed window, any teacher.</param>
public sealed record WindowRecords(
    IReadOnlyList<RecheckRecord> Rechecks, IReadOnlyList<RecheckRecord> PreviousRechecks, IReadOnlyList<PlantRecord> Plants);

/// <summary>A release a person has been put in front of: the standing it was judged from, where it would go, and whether the person declined it.</summary>
/// <param name="From">The committed standing the release was judged from.</param>
/// <param name="To">The state the release would move the slice to.</param>
/// <param name="Declined">A person said no — the same release is not put forward again until the evidence proposes a different one.</param>
public sealed record ReleaseCall(SliceStanding From, TrustState To, bool Declined = false);

/// <summary>What the driver keeps between windows — nothing else. Standings are the committed ones; a release awaiting approval is not in it until a person approves and the caller commits it.</summary>
public sealed record WindowCloseState
{
    /// <summary>Nothing kept yet: every slice starts in shadow, no teacher was qualified.</summary>
    public static WindowCloseState Empty { get; } = new();

    /// <summary>Committed standings by <see cref="Key"/>.</summary>
    public IReadOnlyDictionary<string, SliceStanding> Standings { get; init; } = new Dictionary<string, SliceStanding>();

    /// <summary>Whether each teacher tier was qualified at the last close (what the qualification margin is held against).</summary>
    public IReadOnlyDictionary<string, bool> QualifiedTeachers { get; init; } = new Dictionary<string, bool>();

    /// <summary>
    /// The releases already put in front of a person, by <see cref="Key"/>: a close that judges the same release again does not ask again
    /// (<see cref="WindowCloseResult.ToAsk"/>), and a declined one stays declined while the evidence keeps proposing exactly it. An entry goes when
    /// the slice no longer awaits a release or awaits a different one, so a release that comes back later is a new question.
    /// </summary>
    public IReadOnlyDictionary<string, ReleaseCall> Calls { get; init; } = new Dictionary<string, ReleaseCall>();

    /// <summary>The key of a tier in a slice in <see cref="Standings"/>.</summary>
    public static string Key(string tierId, string sliceKey) => tierId + "\u001f" + sliceKey;

    /// <summary>
    /// The state after a person approved <paramref name="waiting"/>: the slice keeps the standing the release would become
    /// (<see cref="StandingChange.Standing"/>). Refused when the outcome is not a release awaiting approval, or when the kept standing
    /// is no longer the one the release was judged from — a later close or another approval moved the slice on, and committing the
    /// old judgement would release a tier on evidence that is out of date.
    /// </summary>
    /// <param name="waiting">An outcome of <see cref="ControllerStep.AwaitingApproval"/> from the close this state came out of (or a later one that left it waiting).</param>
    public WindowCloseState Approve(SliceOutcome waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        if (!waiting.Step.AwaitsApproval)
            throw new ArgumentException($"Tier '{waiting.TierId}' in slice '{waiting.SliceKey}' has no release waiting for an approval.", nameof(waiting));
        var key = Key(waiting.TierId, waiting.SliceKey);
        if (!Standings.TryGetValue(key, out var kept) || kept != waiting.Committed)
            throw new InvalidOperationException(
                $"Tier '{waiting.TierId}' in slice '{waiting.SliceKey}' is no longer where the release was judged from; close the window again.");
        var standings = Standings.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        standings[key] = waiting.Step.Standing;
        var calls = Calls.Where(kv => kv.Key != key).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return this with { Standings = standings, Calls = calls };
    }

    /// <summary>
    /// The state after a person declined <paramref name="waiting"/>: the standing stays as it is and the same release is not put forward
    /// again (<see cref="WindowCloseResult.ToAsk"/>) while the evidence keeps proposing it. Refused on the same terms as <see cref="Approve"/>.
    /// </summary>
    public WindowCloseState Reject(SliceOutcome waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        if (!waiting.Step.AwaitsApproval)
            throw new ArgumentException($"Tier '{waiting.TierId}' in slice '{waiting.SliceKey}' has no release waiting for an approval.", nameof(waiting));
        var key = Key(waiting.TierId, waiting.SliceKey);
        if (!Standings.TryGetValue(key, out var kept) || kept != waiting.Committed)
            throw new InvalidOperationException(
                $"Tier '{waiting.TierId}' in slice '{waiting.SliceKey}' is no longer where the release was judged from; close the window again.");
        var calls = Calls.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        calls[key] = new ReleaseCall(kept, waiting.Step.Standing.State, Declined: true);
        return this with { Calls = calls };
    }
}

/// <summary>A teacher's standing at the close.</summary>
/// <param name="TierId">The teacher tier.</param>
/// <param name="Qualified">Qualified to teach after this window (with the margin held against <paramref name="WasQualified"/>).</param>
/// <param name="WasQualified">Qualified at the last close.</param>
/// <param name="Disqualified">Shown unqualified — the most its plants allow is under the floor less the margin.</param>
/// <param name="Cell">The plants' cell.</param>
public sealed record TeacherOutcome(string TierId, bool Qualified, bool WasQualified, bool Disqualified, TeacherCell Cell);

/// <summary>The retraining decision for a learning tier — what to do is the consumer's (<see cref="IModelLifecycle.StartAsync"/>).</summary>
public sealed record RetrainOutcome(string TierId, RetrainDecision Decision);

/// <summary>A window's close: the controller's step, the teachers' standing, the retrain decisions and the state to keep.</summary>
/// <param name="Step">The snapshot to send to sites, what each slice got and why, each pool's sharing.</param>
/// <param name="Teachers">Each teacher the slices name.</param>
/// <param name="Retrains">Each learner's decision.</param>
/// <param name="State">The state to keep — committed standings, teacher qualification and the releases already put in front of a person.</param>
public sealed record WindowCloseResult(
    ControllerStep Step, IReadOnlyList<TeacherOutcome> Teachers, IReadOnlyList<RetrainOutcome> Retrains, WindowCloseState State)
{
    /// <summary>
    /// The releases awaiting a person that this close is the first to judge: a release an earlier close already put forward, or one a person
    /// declined, is in <see cref="ControllerStep.AwaitingApproval"/> (the judgement still stands) but not here, so a person is not asked twice.
    /// </summary>
    public IReadOnlyList<SliceOutcome> ToAsk { get; init; } = [];
}

/// <summary>
/// «A window closes, once»: the ledger is computed, each teacher is judged, the lifecycle moves and the snapshot is issued
/// (<see cref="TrustController.Close"/>), and each learning tier's retraining is decided (<see cref="RetrainPolicy.Decide"/>) — as a
/// pure function of the evidence, the state and what people set. No model is called, no clock is read, nothing is stored.
/// </summary>
/// <remarks>
/// <para>
/// It applies only the numbers people set (the controller policy, the teacher's floor, each learner's retrain policy) and invents no
/// threshold. The same evidence, state and inputs give the same result. The manual paths are not bypassed: a release awaits a
/// person unless <see cref="LifecyclePolicy.AutoRelease"/> is on, a frozen slice is held, tightening is committed at once — all of
/// it is <see cref="TrustController"/>'s, which this calls unchanged. Hosting and scheduling are the consumer's
/// (<see cref="WindowCloser"/> reads the stores and keeps the state).
/// </para>
/// <para>
/// A teacher's change of version is read from the recheck records themselves (<see cref="TeacherVersions.Changed"/> against the
/// stretch before), and a teacher shown unqualified or changed tightens the tiers it taught (<see cref="LiveSignals"/>).
/// A learning tier counts as demoted when a slice of it moved down within the same version of the tier; a restart on a new version
/// is not a demotion, or every retraining would cause the next one.
/// </para>
/// </remarks>
public static class WindowClose
{
    /// <summary>Closes the window ending at <see cref="WindowCloseInput.Now"/>.</summary>
    public static WindowCloseResult Run(WindowCloseInput input, WindowRecords records, WindowCloseState state)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(state);
        if (input.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(input), input.Window, "The window has a length.");
        var from = input.Now - input.Window;

        // The teachers: one qualification per teacher tier, from plants of the closed window at the teacher's current version.
        var teachers = new Dictionary<string, TeacherOutcome>(StringComparer.Ordinal);
        foreach (var group in input.Slices.Where(s => s.TeacherTierId is not null).GroupBy(s => s.TeacherTierId!, StringComparer.Ordinal))
        {
            var versions = group.Select(s => s.TeacherVersion).Where(v => v is not null).Distinct(StringComparer.Ordinal).ToList();
            if (versions.Count > 1)
                throw new ArgumentException($"Teacher '{group.Key}' is at versions {string.Join(", ", versions)} in the same close.", nameof(input));
            var cell = TeacherLedger.Compute(records.Plants, group.Key, from, input.Now, sliceKey: null, input.Teacher.Confidence, judgedBy: null,
                tierVersion: versions.SingleOrDefault());
            var was = state.QualifiedTeachers.GetValueOrDefault(group.Key);
            teachers[group.Key] = new TeacherOutcome(group.Key,
                TeacherLedger.Qualifies(cell, input.Teacher.MinimumRecall, input.Teacher.Margin, was), was,
                TeacherLedger.Disqualifies(cell, input.Teacher.MinimumRecall, input.Teacher.Margin), cell);
        }

        var windows = new List<SliceWindow>(input.Slices.Count);
        foreach (var slice in input.Slices)
        {
            var standing = state.Standings.GetValueOrDefault(WindowCloseState.Key(slice.TierId, slice.SliceKey)) ?? SliceStanding.New(slice.TierVersion, input.Now);
            var cell = TrustLedger.Compute(records.Rechecks, slice.TierId, slice.SliceKey, from, input.Now, slice.Caught, input.Confidence);
            var teacher = slice.TeacherTierId is { } id ? teachers[id] : null;
            var versionChanged = TeacherVersions.Changed(
                records.PreviousRechecks.Where(r => r.TierId == slice.TierId && r.SliceKey == slice.SliceKey),
                records.Rechecks.Where(r => r.TierId == slice.TierId && r.SliceKey == slice.SliceKey));
            var signals = new LiveSignals(slice.TierVersion, slice.Drifted, slice.VersionProvenNoWorse,
                TeacherDisqualified: teacher?.Disqualified ?? false, TeacherVersionChanged: versionChanged);
            windows.Add(new SliceWindow(slice.TierId, slice.SliceKey, slice.Pool, standing, cell, teacher?.Qualified ?? true, signals, slice.Traffic)
            {
                OtherKnobRates = slice.OtherKnobRates,
                Freeze = slice.Freeze,
                Selection = slice.Selection,
                AbstainQuantile = slice.AbstainQuantile,
                AbstainExpectedLossAbove = slice.AbstainExpectedLossAbove,
                Calibrated = slice.Calibrated,
            });
        }

        var step = TrustController.Close(input.Version, input.Now, windows, input.Policy, input.SliceKeyVersion);

        var standings = state.Standings.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var outcome in step.Slices)
            standings[WindowCloseState.Key(outcome.TierId, outcome.SliceKey)] = outcome.Committed;
        var qualified = state.QualifiedTeachers.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var teacher in teachers.Values)
            qualified[teacher.TierId] = teacher.Qualified;

        var retrains = new List<RetrainOutcome>(input.Learners.Count);
        foreach (var learner in input.Learners)
        {
            var mine = input.Slices.Where(s => s.TierId == learner.TierId).ToList();
            var demoted = step.Slices.Any(o => o.TierId == learner.TierId && o.Step.To < o.Step.From
                && state.Standings.TryGetValue(WindowCloseState.Key(o.TierId, o.SliceKey), out var before)
                && mine.Any(s => s.SliceKey == o.SliceKey && string.Equals(s.TierVersion, before.TierVersion, StringComparison.Ordinal)));
            var signals = learner.Signals with
            {
                Stale = learner.Signals.Stale || mine.Any(s => s.Drifted),
                Demoted = learner.Signals.Demoted || demoted,
            };
            retrains.Add(new RetrainOutcome(learner.TierId, learner.Policy.Decide(signals, input.Now)));
        }

        var calls = new Dictionary<string, ReleaseCall>(StringComparer.Ordinal);
        var toAsk = new List<SliceOutcome>();
        foreach (var outcome in step.AwaitingApproval)
        {
            var key = WindowCloseState.Key(outcome.TierId, outcome.SliceKey);
            var call = new ReleaseCall(outcome.Committed, outcome.Step.Standing.State);
            if (state.Calls.TryGetValue(key, out var asked) && asked.From == call.From && asked.To == call.To)
                calls[key] = asked;
            else
            {
                calls[key] = call;
                toAsk.Add(outcome);
            }
        }

        return new WindowCloseResult(step, [.. teachers.Values], retrains,
            new WindowCloseState { Standings = standings, QualifiedTeachers = qualified, Calls = calls })
        { ToAsk = toAsk };
    }
}
