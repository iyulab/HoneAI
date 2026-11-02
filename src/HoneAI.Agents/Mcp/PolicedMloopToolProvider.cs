using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace HoneAI.Agents.Mcp;

/// <summary>
/// An <see cref="IMloopToolProvider"/> that offers only what a <see cref="MloopToolPolicy"/> permits: the tools it leaves
/// out are not in the list the agent is built from, and the arguments it forbids are neither in a tool's schema nor
/// accepted when a call sets them.
/// </summary>
/// <param name="inner">Where the tools come from.</param>
/// <param name="policy">What of them may be used.</param>
public sealed class PolicedMloopToolProvider(IMloopToolProvider inner, MloopToolPolicy policy) : IMloopToolProvider
{
    private readonly IMloopToolProvider _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly MloopToolPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await _inner.GetToolsAsync(cancellationToken);
        var offered = new List<AITool>();
        foreach (var tool in tools)
        {
            if (!_policy.Permits(tool.Name))
                continue;
            var forbidden = _policy.ForbiddenArgumentsOf(tool.Name);
            offered.Add(forbidden.Count > 0 && tool is AIFunction function ? new ArgumentGuardedFunction(function, forbidden) : tool);
        }

        // A policy that leaves nothing would start the agent without tools, the failure the unpoliced provider already refuses.
        return offered.Count > 0
            ? offered
            : throw new InvalidOperationException("The tool policy leaves no tool for the agent to call; check the names it allows or denies.");
    }

    /// <summary>A tool offered without some of its arguments, which it refuses when a call sets one.</summary>
    internal sealed class ArgumentGuardedFunction : DelegatingAIFunction
    {
        private readonly IReadOnlySet<string> _forbidden;
        private readonly JsonElement _schema;

        public ArgumentGuardedFunction(AIFunction inner, IReadOnlySet<string> forbidden)
            : base(inner)
        {
            _forbidden = forbidden;
            _schema = WithoutProperties(inner.JsonSchema, forbidden);
        }

        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            foreach (var name in _forbidden.Order(StringComparer.Ordinal))
            {
                if (arguments.TryGetValue(name, out var value) && IsSet(value))
                    return ValueTask.FromResult<object?>(
                        $"Refused: the argument '{name}' of {Name} is not available to you, and nothing was run. Call {Name} without it, or leave the step to someone who may.");
            }

            return base.InvokeCoreAsync(arguments, cancellationToken);
        }

        /// <summary>Whether a call sets the argument: anything but absent, <c>null</c>, <c>false</c>, zero or an empty text.</summary>
        internal static bool IsSet(object? value) => value switch
        {
            null => false,
            bool flag => flag,
            string text => !string.IsNullOrWhiteSpace(text) && !text.Equals("false", StringComparison.OrdinalIgnoreCase),
            JsonElement element => element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
                JsonValueKind.String => IsSet(element.GetString()),
                JsonValueKind.Number => element.TryGetDouble(out var number) && number != 0,
                _ => true,
            },
            JsonNode node => IsSet(JsonSerializer.SerializeToElement(node)),
            IConvertible number when number is not string => Convert.ToDouble(number, System.Globalization.CultureInfo.InvariantCulture) != 0,
            _ => true,
        };

        private static JsonElement WithoutProperties(JsonElement schema, IReadOnlySet<string> names)
        {
            if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root)
                return schema;
            if (root["properties"] is JsonObject properties)
            {
                foreach (var name in names)
                    properties.Remove(name);
            }

            if (root["required"] is JsonArray required)
            {
                for (var i = required.Count - 1; i >= 0; i--)
                {
                    if (required[i]?.GetValue<string>() is { } requiredName && names.Contains(requiredName))
                        required.RemoveAt(i);
                }
            }

            return JsonSerializer.SerializeToElement(root);
        }
    }
}
