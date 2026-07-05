# HoneAI.Agents

MLOps agent engine wiring for [HoneAI](https://github.com/iyulab/HoneAI) — hosts
[IronHive.Agent](https://www.nuget.org/packages/IronHive.Agent)'s `AgentLoop` over
[mloop-mcp](https://github.com/iyulab/mloop-mcp) tools, driven by a system prompt (persona) the
consumer supplies. The engine is **persona-neutral**: HoneAI.Agents owns the agent loop and MCP
tool wiring, while the persona (e.g. the MLOps autonomous-FE Orchestrator) lives in
`HoneAI.Core.RolePersona` and is passed in as `MloopAgentOptions.SystemPrompt`.

Relocated from the (now-deprecated) `mloop-agent` package as part of the HoneAI role-play ②a promotion.

## Usage

```csharp
using HoneAI.Agents;
using HoneAI.Agents.Mcp;

// The consumer supplies the persona (typically HoneAI.Core RolePersona) as the system prompt.
await using var tools = new McpMloopToolProvider(
    mcpEntryPath: "mcp/build/index.js",   // built mloop-mcp entry
    mloopPath:    "/path/to/mloop");       // mloop executable (via MLOOP_PATH)

var agent = await MloopAgent.CreateAsync(
    new MloopAgentOptions
    {
        ChatClient  = chatClient,          // any Microsoft.Extensions.AI IChatClient
        SystemPrompt = systemPrompt,       // e.g. RolePersona.BuildInstructions(orchestratorRole)
    },
    tools);

await foreach (var chunk in agent.RunStreamingAsync("이 프로젝트의 데이터로 자율 FE 루프를 시작하세요."))
{
    // render chunk (text / tool-call deltas)
}
```

## Types

- **`MloopAgent`** — wraps IronHive.Agent's `AgentLoop`; multi-turn history across `RunAsync`/`RunStreamingAsync`.
- **`MloopAgentOptions`** — `ChatClient` + required `SystemPrompt` (persona), optional `ModelId`/`Temperature`/`MaxTokens`.
- **`IMloopToolProvider`** / **`McpMloopToolProvider`** — connects mloop-mcp as an MCP stdio plugin and exposes its tools (fail-fast if zero tools).

## License

Apache-2.0
