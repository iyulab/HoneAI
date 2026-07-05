using Microsoft.Extensions.AI;
using Xunit;

namespace HoneAI.Agents.Tests;

public class MloopAgentOptionsTests
{
    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Required_props_are_set_and_optionals_default_null()
    {
        var options = new MloopAgentOptions
        {
            ChatClient = new NoopChatClient(),
            SystemPrompt = "SYS",
        };

        Assert.Equal("SYS", options.SystemPrompt);
        Assert.NotNull(options.ChatClient);
        Assert.Null(options.ModelId);
        Assert.Null(options.Temperature);
        Assert.Null(options.MaxTokens);
    }
}
