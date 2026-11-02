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
        var seen = previous
            .Where(r => r.CheckedBy is not null && r.CheckedByVersion is not null)
            .GroupBy(r => r.CheckedBy!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.CheckedByVersion!).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        return current.Any(r => r.CheckedBy is not null
            && r.CheckedByVersion is not null
            && seen.TryGetValue(r.CheckedBy, out var versions)
            && !versions.Contains(r.CheckedByVersion));
    }
}
