namespace HoneAI;

/// <summary>
/// The domain-colour injection slot for a trust-loop role. The middleware never
/// <i>interprets</i> <see cref="Domain"/> — it threads it into the role's persona
/// skeleton (the <c>{Domain}</c> slot of an <c>IAgent.Instructions</c>) exactly the way
/// <see cref="PredictionProvenance.Annotations"/> carries consumer-defined fields
/// opaquely (design spec §3, "Annotations 패턴"). Manufacturing meaning — "전해탈지
/// 공정 이상탐지", verdict vocabulary — is supplied by the consumer/harness, never baked
/// into HoneAI.
/// </summary>
public sealed record RoleContext
{
    /// <summary>Which trust-loop seat this context binds an agent to.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>
    /// Consumer-injected domain colour, e.g. "전해탈지 공정 이상탐지". Opaque to the
    /// middleware; it fills the persona skeleton's domain slot.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Optional ML task hint (e.g. "anomaly"/"binary"/"regression") so a role can adapt
    /// its persona to the task family; <see langword="null"/> when task-agnostic.
    /// </summary>
    public string? TaskType { get; init; }
}
