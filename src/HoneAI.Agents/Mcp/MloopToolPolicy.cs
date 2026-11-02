namespace HoneAI.Agents.Mcp;

/// <summary>
/// Which of the mloop-mcp tools an agent may call, and which arguments of those it may not use — what a seat is allowed
/// to do, decided by structure and not by what its prompt asks of it. A prompt that says "stop before training" is a
/// request; a tool the agent is never given cannot be called, however the conversation goes.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: <see cref="Everything"/> is the behavior without a policy, every tool and every argument. Applied through
/// <see cref="PolicedMloopToolProvider"/>, which wraps any <see cref="IMloopToolProvider"/>.
/// </para>
/// <para>
/// Tool names are matched as they are listed (<c>mloop_promote</c>), also behind a prefix a tool host adds
/// (<c>mloop__mloop_promote</c>). A tool the policy does not name is allowed unless an allow-list is set
/// (<see cref="AllowOnly"/>); a denial list therefore does not cover a tool added to mloop-mcp later, and an allow-list does.
/// </para>
/// </remarks>
public sealed class MloopToolPolicy
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.Ordinal);

    private readonly IReadOnlySet<string>? _allowed;
    private readonly IReadOnlySet<string> _denied;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _forbiddenArguments;

    private MloopToolPolicy(
        IReadOnlySet<string>? allowed, IReadOnlySet<string> denied, IReadOnlyDictionary<string, IReadOnlySet<string>> forbiddenArguments)
    {
        _allowed = allowed;
        _denied = denied;
        _forbiddenArguments = forbiddenArguments;
    }

    /// <summary>Every tool and every argument — the behavior without a policy.</summary>
    public static MloopToolPolicy Everything { get; } = new(null, NoNames, new Dictionary<string, IReadOnlySet<string>>());

    /// <summary>
    /// The tools that train a model or move production, left out: <c>mloop_train</c>, <c>mloop_auto_train</c>,
    /// <c>mloop_quick_start</c> (initialize, train and promote in one step), <c>mloop_promote</c> and <c>mloop_rollback</c>.
    /// What a seat that prepares the ground and reports back may hold; the decisions that are hard to take back stay with
    /// someone else.
    /// </summary>
    public static MloopToolPolicy WithoutTrainingOrPromotion { get; } = Everything.Deny(
        "mloop_train", "mloop_auto_train", "mloop_quick_start", "mloop_promote", "mloop_rollback");

    /// <summary>Only <paramref name="tools"/>; any other tool, also one added to mloop-mcp later, is not offered.</summary>
    public static MloopToolPolicy AllowOnly(params string[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return new(new HashSet<string>(tools, StringComparer.Ordinal), NoNames, new Dictionary<string, IReadOnlySet<string>>());
    }

    /// <summary>This policy and also without <paramref name="tools"/>.</summary>
    public MloopToolPolicy Deny(params string[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return new(_allowed, new HashSet<string>(_denied.Concat(tools), StringComparer.Ordinal), _forbiddenArguments);
    }

    /// <summary>
    /// This policy and also with <paramref name="arguments"/> of <paramref name="tool"/> unusable: the tool is offered without
    /// them in its schema, and a call that sets one anyway is refused with a message, not run. An argument set to
    /// <c>false</c> or <c>null</c> is not set.
    /// </summary>
    public MloopToolPolicy ForbidArguments(string tool, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        var merged = _forbiddenArguments.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        merged[tool] = new HashSet<string>((merged.TryGetValue(tool, out var already) ? already : NoNames).Concat(arguments), StringComparer.Ordinal);
        return new(_allowed, _denied, merged);
    }

    /// <summary>Whether the tool named <paramref name="toolName"/> is offered.</summary>
    public bool Permits(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (_denied.Any(n => NameMatches(toolName, n)))
            return false;
        return _allowed is null || _allowed.Any(n => NameMatches(toolName, n));
    }

    /// <summary>The arguments of the tool named <paramref name="toolName"/> that may not be used; empty when none.</summary>
    public IReadOnlySet<string> ForbiddenArgumentsOf(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tool, arguments) in _forbiddenArguments)
        {
            if (NameMatches(toolName, tool))
                forbidden.UnionWith(arguments);
        }

        return forbidden;
    }

    // The name as listed, or behind a prefix a host puts before it ("mloop__mloop_promote") — never a longer name that only
    // ends the same way ("mloop_auto_train" is not "mloop_train").
    internal static bool NameMatches(string listed, string policyName)
        => listed == policyName
           || (listed.EndsWith(policyName, StringComparison.Ordinal)
               && listed.Length > policyName.Length
               && !char.IsLetterOrDigit(listed[listed.Length - policyName.Length - 1]));
}
