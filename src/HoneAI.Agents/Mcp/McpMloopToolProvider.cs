using IronHive.Agent.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoneAI.Agents.Mcp;

/// <summary>
/// Connects mloop-mcp as an MCP stdio plugin (`node &lt;entry&gt;`) and exposes its tools.
/// This is how HoneAI.Agents dogfoods mloop-mcp.
/// </summary>
public sealed class McpMloopToolProvider(string mcpEntryPath, string? mloopPath = null, ILogger? logger = null)
    : IMloopToolProvider, IAsyncDisposable
{
    private readonly McpPluginManager _manager = new();
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private bool _connected;

    public static McpPluginConfig BuildConfig(string mcpEntryPath, string? mloopPath) => new()
    {
        Transport = McpTransportType.Stdio,
        Command = "node",
        Arguments = [mcpEntryPath],
        Environment = mloopPath is null
            ? null
            : new Dictionary<string, string> { ["MLOOP_PATH"] = mloopPath },
    };

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            _logger.LogInformation("Connecting mloop-mcp (node {Entry})...", mcpEntryPath);
            await _manager.ConnectAsync("mloop", BuildConfig(mcpEntryPath, mloopPath), cancellationToken);
            _connected = true;
        }

        var tools = await _manager.GetToolsAsync(cancellationToken);
        _logger.LogInformation("mloop-mcp exposed {Count} tool(s).", tools.Count);
        return EnsureToolsLoaded(tools, mcpEntryPath);
    }

    /// <summary>
    /// Fail fast when mloop-mcp connected but exposed no tools. Without this, the agent would start
    /// silently tool-less (the LLM can talk but can never drive MLoop) — a confusing failure mode.
    /// Names the entry so the operator can diagnose (wrong path, mcp build broken, mloop missing).
    /// </summary>
    internal static IReadOnlyList<AITool> EnsureToolsLoaded(IReadOnlyList<AITool> tools, string mcpEntryPath)
        => tools.Count > 0
            ? tools
            : throw new InvalidOperationException(
                $"mloop-mcp ({mcpEntryPath}) 에 연결됐으나 도구를 0개 노출했습니다. " +
                "mcp 진입점/빌드와 MLOOP_PATH 를 확인하세요.");

    public async ValueTask DisposeAsync() => await _manager.DisposeAsync();
}
