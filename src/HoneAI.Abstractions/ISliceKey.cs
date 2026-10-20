namespace HoneAI;

/// <summary>
/// How an input maps to a slice — the unit trust is earned in. Supplied by the consumer: a template
/// key, a device, an inspection slot.
/// </summary>
/// <typeparam name="TInput">What is judged.</typeparam>
public interface ISliceKey<in TInput>
{
    /// <summary>
    /// Version of the key's definition. A changed definition means the slices trust was earned in
    /// are not the slices inputs now fall in.
    /// </summary>
    string Version { get; }

    /// <summary>The slice <paramref name="input"/> falls in.</summary>
    string KeyOf(TInput input);
}
