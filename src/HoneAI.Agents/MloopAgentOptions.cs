using Microsoft.Extensions.AI;

namespace HoneAI.Agents;

/// <summary>
/// Configuration for <see cref="MloopAgent"/>. Provider-agnostic: the consumer supplies the
/// <see cref="IChatClient"/>; HoneAI.Agents never forces a specific LLM provider.
/// </summary>
public sealed class MloopAgentOptions
{
    /// <summary>LLM chat client (any Microsoft.Extensions.AI-compatible provider).</summary>
    public required IChatClient ChatClient { get; init; }

    /// <summary>
    /// The full system prompt (persona) the agent runs under. HoneAI.Agents is persona-neutral engine
    /// wiring, so the consumer supplies this — typically <c>HoneAI.Core.RolePersona.BuildInstructions</c>
    /// for the relevant lifecycle role (e.g. Orchestrator), plus any runtime addenda (project path, etc.).
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Model id, used for token pricing/telemetry. Optional.</summary>
    public string? ModelId { get; init; }

    /// <summary>Sampling temperature. Optional.</summary>
    public float? Temperature { get; init; }

    /// <summary>Max output tokens. Optional.</summary>
    public int? MaxTokens { get; init; }
}
