namespace HoneAI.Trust;

/// <summary>
/// Whether the tier that rechecks a tier has changed version between two stretches of its rechecks — read off the records,
/// so a driver needs to keep nothing but the records it already keeps.
/// </summary>
public static class TeacherVersions
{
    /// <summary>
    /// True when <paramref name="current"/> holds a recheck by a teacher tier at a version that <paramref name="previous"/> never saw
    /// for that same teacher. Records without a version (written before it was kept) and a teacher that <paramref name="previous"/> has no
    /// versioned record of give no basis to compare, so they never make a change: a first stretch of evidence, or a teacher added beside
    /// the old one, is not a new version of the one that was there.
    /// </summary>
    /// <param name="previous">The records of the stretch before — the evidence the tier's standing was earned on.</param>
    /// <param name="current">The records of the stretch being judged.</param>
    public static bool Changed(IEnumerable<RecheckRecord> previous, IEnumerable<RecheckRecord> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return AnyNewVersion(previous.Select(r => (r.CheckedBy, r.CheckedByVersion)), current.Select(r => (r.CheckedBy, r.CheckedByVersion)));
    }

    // The same rule over who judged at what version, for evidence that is not all rechecks of settled decisions.
    internal static bool AnyNewVersion(IEnumerable<(string? By, string? Version)> previous, IEnumerable<(string? By, string? Version)> current)
    {
        var seen = previous
            .Where(j => j.By is not null && j.Version is not null)
            .GroupBy(j => j.By!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(j => j.Version!).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        return current.Any(j => j.By is not null
            && j.Version is not null
            && seen.TryGetValue(j.By, out var versions)
            && !versions.Contains(j.Version));
    }
}
