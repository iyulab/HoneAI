using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HoneAI.Routing;

/// <summary>
/// The recheck draw: a decision is drawn when a uniform number derived from its id and the snapshot
/// version falls below π. Deterministic — the same decision under the same snapshot is drawn the same
/// way on every site and every retry, so a draw can be audited and replayed — and independent of
/// anything a tier answered.
/// </summary>
public static class RecheckDraw
{
    /// <summary>A number in [0, 1) fixed by <paramref name="decisionId"/> and <paramref name="snapshotVersion"/>.</summary>
    public static double Uniform(string decisionId, string snapshotVersion)
    {
        ArgumentNullException.ThrowIfNull(decisionId);
        ArgumentNullException.ThrowIfNull(snapshotVersion);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(decisionId + "\n" + snapshotVersion), hash);
        // 53 bits: the precision of a double's mantissa, so every value is exactly representable.
        return (BinaryPrimitives.ReadUInt64BigEndian(hash) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>True when the decision is drawn at probability <paramref name="probability"/>.</summary>
    public static bool IsDrawn(string decisionId, string snapshotVersion, double probability)
        => Uniform(decisionId, snapshotVersion) < probability;
}
