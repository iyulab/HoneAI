using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Routing;

/// <summary>Reference judges — for tests, examples, and wrapping a plain function as a tier.</summary>
public static class Judge
{
    /// <summary>A judge from a function.</summary>
    public static IJudge<TInput, TValue> From<TInput, TValue>(
        Tier tier, Func<TInput, JudgeContext, CancellationToken, ValueTask<JudgeVerdict<TValue>>> judge)
        => new DelegateJudge<TInput, TValue>(tier ?? throw new ArgumentNullException(nameof(tier)),
            judge ?? throw new ArgumentNullException(nameof(judge)));

    /// <summary>A judge that always answers <paramref name="value"/>.</summary>
    public static IJudge<TInput, TValue> Constant<TInput, TValue>(Tier tier, TValue value, double? confidence = null)
        => From<TInput, TValue>(tier, (_, _, _) => ValueTask.FromResult(JudgeVerdict<TValue>.Answer(value, confidence, "constant")));

    private sealed class DelegateJudge<TInput, TValue>(
        Tier tier, Func<TInput, JudgeContext, CancellationToken, ValueTask<JudgeVerdict<TValue>>> judge) : IJudge<TInput, TValue>
    {
        public Tier Tier => tier;
        public ValueTask<JudgeVerdict<TValue>> JudgeAsync(TInput input, JudgeContext context, CancellationToken cancellationToken = default)
            => judge(input, context, cancellationToken);
    }
}

/// <summary>
/// A <see cref="JudgeKind.System1"/> judge over a memory of settled answers: answers what it has
/// been told for an input's key, abstains otherwise. The simplest cheap tier — and a stand-in for an
/// engine that remembers.
/// </summary>
public sealed class MemoryJudge<TInput, TValue> : IJudge<TInput, TValue>
{
    private readonly Func<TInput, string> _keyOf;
    private readonly ConcurrentDictionary<string, TValue> _memory = new(StringComparer.Ordinal);

    /// <param name="tier">The tier; its kind is normally <see cref="JudgeKind.System1"/>.</param>
    /// <param name="keyOf">The key an input is remembered under.</param>
    public MemoryJudge(Tier tier, Func<TInput, string> keyOf)
    {
        Tier = tier ?? throw new ArgumentNullException(nameof(tier));
        _keyOf = keyOf ?? throw new ArgumentNullException(nameof(keyOf));
    }

    /// <inheritdoc />
    public Tier Tier { get; }

    /// <summary>Remember <paramref name="value"/> as the answer for <paramref name="key"/>.</summary>
    public void Remember(string key, TValue value) => _memory[key] = value;

    /// <inheritdoc />
    public ValueTask<JudgeVerdict<TValue>> JudgeAsync(TInput input, JudgeContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_memory.TryGetValue(_keyOf(input), out var value)
            ? JudgeVerdict<TValue>.Answer(value, rationale: "remembered")
            : JudgeVerdict<TValue>.Abstain("nothing remembered for this key"));
}
