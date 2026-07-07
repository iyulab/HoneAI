using System;

namespace HoneAI.Composition;

/// <summary>
/// Marker for the HoneAI assembly layer. This type exists so the assembly has an
/// explicit, documented home and the import-boundary test has a stable anchor.
/// </summary>
internal static class AssemblyMarker
{
    /// <summary>
    /// The contract floor this assembly layer composes over. Anchors the Core →
    /// Abstractions dependency that the concrete implementations build on.
    /// </summary>
    internal static readonly Type ContractFloor = typeof(PredictionProvenance);
}
