namespace HoneAI;

/// <summary>
/// A prediction value that cannot exist without provenance — the type-level
/// expression of "출처 없는 예측 = 컴파일 불가" (Phase 0 0-5). Every value that
/// flows out of HoneAI carries the <see cref="Provenance"/> of how it was made.
/// </summary>
/// <typeparam name="T">The domain prediction payload (verdict, risk level, forecast, …).</typeparam>
public interface ITracedPrediction<out T>
{
    /// <summary>The predicted value.</summary>
    T Value { get; }

    /// <summary>How this value was produced — always present, never null.</summary>
    PredictionProvenance Provenance { get; }
}

/// <summary>Default immutable carrier for <see cref="ITracedPrediction{T}"/>.</summary>
/// <param name="Value">The predicted value.</param>
/// <param name="Provenance">Required traceability stamp.</param>
public sealed record TracedPrediction<T>(T Value, PredictionProvenance Provenance)
    : ITracedPrediction<T>;
