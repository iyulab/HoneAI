using System;

namespace HoneAI.Composition;

/// <summary>
/// Marker for the HoneAI assembly layer. Phase 0 is scaffold-only: the concrete
/// reasoning router, provenance sink, MLoop client, HITL gate, and lifecycle
/// orchestrator land in Phase 1 (roadmap/13 §4). This type exists so the assembly
/// has an explicit, documented home and the import-boundary test has a stable anchor.
/// </summary>
internal static class AssemblyMarker
{
    /// <summary>
    /// The contract floor this assembly layer composes over. Anchors the Core →
    /// Abstractions dependency that Phase 1's concrete implementations will fill in.
    /// </summary>
    internal static readonly Type ContractFloor = typeof(PredictionProvenance);
}
