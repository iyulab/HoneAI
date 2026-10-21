using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>How a teacher judged a planted item — an input whose answer is already known.</summary>
public enum PlantOutcome
{
    /// <summary>It answered, and its answer agrees with the known one (by the settlement policy's predicate).</summary>
    Correct = 0,

    /// <summary>It answered differently.</summary>
    Wrong = 1,

    /// <summary>It gave no answer — abstained, was cut off, timed out.</summary>
    Failed = 2,
}

/// <summary>
/// One planted item a teacher judged. A teacher is the tier rechecks are judged by; agreement with
/// it is not correctness, so before its rechecks count, how often it is right — above all, how often
/// it catches a must-catch value — is measured on items whose answer is known (from a consensus or a
/// person's settled decision), slipped among its real rechecks so it cannot tell them apart.
/// </summary>
public sealed record PlantRecord
{
    /// <summary>The planted item's id — with <see cref="TierId"/> and <see cref="JudgedBy"/>, the key it is recorded under once.</summary>
    public required string PlantId { get; init; }

    /// <summary>The teacher that judged it.</summary>
    public required string TierId { get; init; }

    /// <summary>
    /// Which judge within the tier judged it — the person, on a rung of people, so each is measured on their own
    /// plants. Null for a tier that is one judge.
    /// </summary>
    public string? JudgedBy { get; init; }

    /// <summary>The item's slice.</summary>
    public required string SliceKey { get; init; }

    /// <summary>When the teacher judged it — windows are assigned by this.</summary>
    public required DateTimeOffset JudgedAt { get; init; }

    /// <summary>The known answer is a must-catch value.</summary>
    public required bool TruthMustCatch { get; init; }

    /// <summary>How the teacher did.</summary>
    public required PlantOutcome Outcome { get; init; }

    /// <summary>The teacher answered with a must-catch value. Meaningless when <see cref="Outcome"/> is <see cref="PlantOutcome.Failed"/>.</summary>
    public bool AnsweredMustCatch { get; init; }

    /// <summary>Where the known answer came from (a consensus, a person's settled decision) — for the audit.</summary>
    public string? TruthSource { get; init; }

    /// <summary>The teacher's version when it judged.</summary>
    public string? TierVersion { get; init; }
}

/// <summary>Where planted-item judgments are kept and read back — the counterpart of <see cref="IRecheckStore"/> for teachers.</summary>
public interface IPlantStore
{
    /// <summary>Records <paramref name="record"/> unless one for the same plant, tier and judge exists; returns whether it was recorded.</summary>
    ValueTask<bool> AppendAsync(PlantRecord record, CancellationToken cancellationToken = default);

    /// <summary>The records of <paramref name="tierId"/> judged in [<paramref name="from"/>, <paramref name="to"/>), every slice.</summary>
    IAsyncEnumerable<PlantRecord> ReadAsync(
        string tierId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

/// <summary>
/// A teacher's measured standing over a window: how often it catches a must-catch value, and how
/// often it is right, on planted items. Planted sets are built heavy in hard and risky items to
/// measure recall quickly, so <see cref="Accuracy"/> is on the planted set — not the teacher's
/// accuracy on traffic.
/// </summary>
public sealed record TeacherCell
{
    public required string TierId { get; init; }

    /// <summary>The judge within the tier the cell is restricted to; null for every judge of the tier pooled.</summary>
    public string? JudgedBy { get; init; }

    /// <summary>The slice the cell is restricted to; null for all of them.</summary>
    public string? SliceKey { get; init; }

    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>Planted items judged, failed ones included.</summary>
    public required int Plants { get; init; }

    /// <summary>Planted items the teacher did not answer.</summary>
    public required int Failures { get; init; }

    /// <summary>Planted items whose known answer is a must-catch value.</summary>
    public required int MustCatchPlants { get; init; }

    /// <summary>Of those, the ones the teacher answered with a must-catch value.</summary>
    public required int MustCatchCaught { get; init; }

    /// <summary>Of those, the ones the teacher did not answer.</summary>
    public required int MustCatchFailures { get; init; }

    /// <summary>Must-catch recall over the answered must-catch plants. Null with none answered.</summary>
    public double? Recall { get; init; }

    /// <summary>
    /// Exact (Clopper–Pearson) lower bound on must-catch recall at <see cref="Confidence"/>, with every
    /// failed must-catch plant counted as missed. What qualification is judged on. Null without a must-catch plant.
    /// </summary>
    public double? RecallLower { get; init; }

    /// <summary>Share of answered plants answered correctly. Null with none answered.</summary>
    public double? Accuracy { get; init; }

    /// <summary>Exact lower bound on accuracy, failures counted as wrong. Null without a plant.</summary>
    public double? AccuracyLower { get; init; }

    /// <summary>The confidence the bounds are stated at.</summary>
    public required double Confidence { get; init; }
}
