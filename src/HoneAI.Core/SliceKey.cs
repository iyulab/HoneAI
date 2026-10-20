using System;

namespace HoneAI;

/// <summary>Ready-made <see cref="ISliceKey{TInput}"/> shapes.</summary>
public static class SliceKey
{
    /// <summary>The key for "one slice for everything".</summary>
    public const string Everything = "*";

    /// <summary>A slice key from a function, with the version of its definition.</summary>
    public static ISliceKey<TInput> From<TInput>(string version, Func<TInput, string> keyOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(keyOf);
        return new DelegateKey<TInput>(version, keyOf);
    }

    /// <summary>Every input in one slice — trust is earned once, for all of it.</summary>
    public static ISliceKey<TInput> Single<TInput>() => new DelegateKey<TInput>("single", _ => Everything);

    private sealed class DelegateKey<TInput>(string version, Func<TInput, string> keyOf) : ISliceKey<TInput>
    {
        public string Version => version;
        public string KeyOf(TInput input) => keyOf(input);
    }
}
