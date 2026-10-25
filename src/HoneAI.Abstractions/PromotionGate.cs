namespace HoneAI;

/// <summary>What a promotion gate decided for a trained model.</summary>
public enum PromotionDecision
{
    /// <summary>Put it in production.</summary>
    Promote = 0,

    /// <summary>Keep production as it is; the run completes without promotion.</summary>
    Refuse = 1,

    /// <summary>Not decided yet — a person has not reviewed it, a shadow has not earned its standing. The run waits and resumes.</summary>
    Wait = 2,
}

/// <summary>A trained model a promotion gate decides on.</summary>
/// <param name="RunId">The lifecycle run that trained it.</param>
/// <param name="Model">The MLoop model name.</param>
/// <param name="ExperimentId">The experiment the training produced.</param>
public sealed record PromotionCandidate(string RunId, string Model, string ExperimentId)
{
    /// <summary>The id a review of it is kept under (<see cref="LifecycleRequest.ReviewId"/>, or the run id).</summary>
    public string? ReviewId { get; init; }

    /// <summary>Its metrics on MLoop's own held-out rows.</summary>
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }

    /// <summary>MLoop's warnings about it.</summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>When its training completed.</summary>
    public DateTimeOffset? TrainedAt { get; init; }

    /// <summary>What it learned from, when the run was given it (<see cref="LifecycleRequest.TrainingSet"/>).</summary>
    public TrainingSetManifest? TrainingSet { get; init; }
}

/// <summary>A promotion gate's decision, and why.</summary>
/// <param name="Decision">What it decided.</param>
/// <param name="Reason">Why — for the run's trail; what the decision rests on, in words a person can check.</param>
/// <param name="DecidedBy">Who decided: <see cref="JudgeKind.Human"/> for a review, <see cref="JudgeKind.Statistics"/> for evidence.</param>
public sealed record PromotionVerdict(PromotionDecision Decision, string? Reason, JudgeKind DecidedBy)
{
    /// <summary>
    /// The candidate was shown no worse than the model in production — the evidence a tier's new version keeps its
    /// standing on (<c>LiveSignals.VersionProvenNoWorse</c>) instead of starting again in shadow.
    /// </summary>
    public bool ProvenNoWorse { get; init; }
}

/// <summary>
/// Decides whether a trained model goes to production — the review stage of <see cref="IModelLifecycle"/>. A person's
/// review is one gate (the default); evidence on held-out labels and a shadow's earned standing are others. A gate
/// that cannot decide yet answers <see cref="PromotionDecision.Wait"/>, and the run asks it again when resumed — so it
/// must answer the same candidate the same way, without side effects beyond the first ask (opening a review once).
/// </summary>
public interface IPromotionGate
{
    /// <summary>Decides on <paramref name="candidate"/>.</summary>
    Task<PromotionVerdict> JudgeAsync(PromotionCandidate candidate, CancellationToken cancellationToken = default);
}
