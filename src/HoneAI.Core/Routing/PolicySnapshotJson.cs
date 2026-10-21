using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoneAI.Routing;

/// <summary>
/// The canonical JSON form of a <see cref="PolicySnapshot"/>, and its content hash. Canonical means
/// one byte sequence per snapshot — entries ordered by tier and slice, rates by knob, times in UTC —
/// so the hash names the content: a site can check that the snapshot it holds is the one issued, and
/// a signature (the consumer's) can be laid over the bytes.
/// </summary>
public static class PolicySnapshotJson
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The canonical UTF-8 JSON of <paramref name="snapshot"/>.</summary>
    public static byte[] Serialize(PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("version", snapshot.Version);
            w.WriteString("issuedAt", Utc(snapshot.IssuedAt));
            w.WriteString("softExpiresAt", Utc(snapshot.SoftExpiresAt));
            w.WriteString("hardExpiresAt", Utc(snapshot.HardExpiresAt));
            if (snapshot.SliceKeyVersion is { } skv)
                w.WriteString("sliceKeyVersion", skv);
            w.WriteStartArray("entries");
            foreach (var e in snapshot.Entries
                         .OrderBy(e => e.TierId, StringComparer.Ordinal)
                         .ThenBy(e => e.SliceKey, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("tierId", e.TierId);
                w.WriteString("sliceKey", e.SliceKey);
                w.WriteString("state", e.State.ToString());
                if (e.SampleRates is { Count: > 0 } rates)
                {
                    w.WriteStartObject("sampleRates");
                    foreach (var (knob, rate) in rates.OrderBy(r => r.Key, StringComparer.Ordinal))
                        w.WriteNumber(knob, rate);
                    w.WriteEndObject();
                }
                if (e.Selection is { Count: > 0 } selection)
                {
                    w.WriteStartObject("selection");
                    foreach (var (knob, sel) in selection.OrderBy(r => r.Key, StringComparer.Ordinal))
                    {
                        w.WriteStartObject(knob);
                        w.WriteString("shape", sel.Shape.ToString());
                        w.WriteStartArray("calibration");
                        foreach (var bin in sel.Calibration.OrderBy(b => b.UpTo))
                        {
                            w.WriteStartObject();
                            w.WriteNumber("upTo", bin.UpTo);
                            w.WriteNumber("probability", bin.Probability);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteNumber("meanScore", sel.MeanScore);
                        w.WriteNumber("uniformMix", sel.UniformMix);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                if (e.AbstainQuantile is { } q)
                    w.WriteNumber("abstainQuantile", q);
                if (e.RateSetBy is { } by)
                    w.WriteString("rateSetBy", by);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Reads a snapshot from JSON (canonical or not).</summary>
    public static PolicySnapshot Deserialize(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize<PolicySnapshot>(json, ReadOptions)
           ?? throw new JsonException("The JSON holds no policy snapshot.");

    /// <summary>
    /// The content hash — lowercase hex SHA-256 of the canonical JSON. Two snapshots with the same
    /// content have the same hash, however their entries were ordered when built.
    /// </summary>
    public static string ContentHash(PolicySnapshot snapshot)
        => Convert.ToHexStringLower(SHA256.HashData(Serialize(snapshot)));

    private static string Utc(DateTimeOffset t)
        => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
