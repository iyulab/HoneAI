using HoneAI.Agents.Mcp;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace HoneAI.Agents;

/// <summary>
/// MLOps agent: wraps IronHive.Agent's <see cref="AgentLoop"/>, runs under the consumer-supplied
/// system prompt (persona), and exposes mloop-mcp tools to the LLM. The single <see cref="AgentLoop"/>
/// instance keeps conversation history across <see cref="RunAsync"/> calls (multi-turn).
///
/// Persona ownership lives with the consumer (HoneAI.Core RolePersona), not here — this type is the
/// role-neutral engine/tool wiring the ②-family roles share.
/// </summary>
public sealed class MloopAgent
{
    private readonly IAgentLoop _loop;

    private MloopAgent(IAgentLoop loop) => _loop = loop;

    public static async Task<MloopAgent> CreateAsync(
        MloopAgentOptions options,
        IMloopToolProvider toolProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(toolProvider);

        var tools = await toolProvider.GetToolsAsync(cancellationToken);

        // Wrap the consumer's plain client so M.E.AI auto-executes tool calls within a turn.
        var chatClient = options.ChatClient.AsBuilder().UseFunctionInvocation().Build();

        var agentOptions = new AgentOptions
        {
            SystemPrompt = options.SystemPrompt,
            ModelId = options.ModelId,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            Tools = [.. tools],
        };

        var loop = new AgentLoop(chatClient, agentOptions);
        return new MloopAgent(loop);
    }

    /// <summary>Runs one user turn and returns the full response.</summary>
    public Task<AgentResponse> RunAsync(string prompt, CancellationToken cancellationToken = default)
        => _loop.RunAsync(prompt, cancellationToken);

    /// <summary>Runs one user turn, streaming response chunks.</summary>
    public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(string prompt, CancellationToken cancellationToken = default)
        => _loop.RunStreamingAsync(prompt, cancellationToken);
}
