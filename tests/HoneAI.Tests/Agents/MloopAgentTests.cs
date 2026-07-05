using HoneAI.Agents.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace HoneAI.Agents.Tests;

public class MloopAgentTests
{
    // HoneAI.Agents is persona-neutral: the consumer always supplies the system prompt. A marker
    // stands in for what HoneAI.Core RolePersona would produce, so we can assert verbatim pass-through.
    private const string Persona = "PERSONA-MARKER: MLOps 어시스턴트, mloop_train 사용, 프로젝트 C:/kamp/seq004";

    private static MloopAgentOptions Opts(IChatClient client) => new()
    {
        ChatClient = client,
        SystemPrompt = Persona,
    };

    [Fact]
    public async Task RunAsync_returns_assistant_content()
    {
        var agent = await MloopAgent.CreateAsync(Opts(new CapturingChatClient()), new StubToolProvider());

        var response = await agent.RunAsync("이 데이터로 모델 만들어줘");

        Assert.Equal("OK", response.Content);
    }

    [Fact]
    public async Task CreateAsync_injects_supplied_persona_and_wires_tools()
    {
        var capturing = new CapturingChatClient();
        var agent = await MloopAgent.CreateAsync(Opts(capturing), new StubToolProvider());

        await agent.RunAsync("시작");

        // 소비자가 공급한 페르소나가 첫 시스템 메시지로 verbatim 주입됨(엔진은 페르소나-중립).
        var system = Assert.Single(capturing.LastMessages!, m => m.Role == ChatRole.System);
        Assert.Equal(Persona, system.Text);

        // MCP 도구가 루프에 전달됨.
        Assert.NotNull(capturing.LastOptions?.Tools);
        Assert.Contains(capturing.LastOptions!.Tools!, t => t.Name == "mloop_info_stub");
    }
}
