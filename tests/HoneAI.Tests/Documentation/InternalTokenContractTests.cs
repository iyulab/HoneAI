using System.Text.RegularExpressions;
using Xunit;

namespace HoneAI.Tests.Documentation;

/// <summary>
/// Nothing this repository publishes carries a token that only means something inside the
/// workspace it was written in — an internal work-item or design-decision id, a path off one
/// machine's disk, a pointer into a private notes tree.
/// </summary>
/// <remarks>
/// <para>
/// This repository is public, and so are its packages. A reader has no tracker to look <c>D-18</c>
/// up in and cannot follow a link into a directory that exists only on a maintainer's machine — the
/// token spends their attention and returns nothing, and it leaks the shape of a private workspace.
/// </para>
/// <para>
/// The rule is enforced by a regex rather than by careful reading because careful reading misses
/// tokens that look ordinary everywhere else. What the guard does not do is decide what replaces a
/// token it finds — deleting a bare cross-reference is usually right, a marker that ties call sites
/// together needs a phrase that says what the tie is. Names of the projects that consume this
/// library are not listed here, deliberately: a list of them in a public file would be the leak it
/// guards against. Those stay with review.
/// </para>
/// </remarks>
public class InternalTokenContractTests
{
    /// <summary>
    /// Prefixes that look like an internal id but are public vocabulary: encodings and standards
    /// bodies, hash algorithms (<c>SHA-256</c>), ADR numbering, and <c>N</c> as a count in a formula.
    /// </summary>
    private static readonly HashSet<string> PublicPrefixes =
        new(StringComparer.Ordinal) { "UTF", "ISO", "CP", "CVE", "RFC", "ADR", "SHA", "N" };

    /// <summary>
    /// An internal id: a short upper-case prefix (or the literal <c>cycle</c>) and a number.
    /// Shape-matched rather than enumerated, because the prefixes in use are not a closed set.
    /// Upper-case is the whole discrimination — <c>t-1</c> and <c>run-1</c> are fixture data.
    /// </summary>
    private static readonly Regex WorkItemId =
        new(@"\b(?<prefix>[A-Z]{1,5}|cycle)-\d+\b", RegexOptions.Compiled);

    /// <summary>Internal units of work whose name is an ordinary word: a development cycle, a sprint.</summary>
    private static readonly Regex InternalWorkUnit =
        new(@"\b(?:[Cc]ycle|[Ss]print)[- ]\d+\b", RegexOptions.Compiled);

    /// <summary>
    /// An absolute path on someone's machine, any drive letter. Applied to prose only — in C# a
    /// drive path can be the input a test is about.
    /// </summary>
    private static readonly Regex DriveLetterPath = new(@"\b[A-Za-z]:\\", RegexOptions.Compiled);

    /// <summary>Names of the private working tree this package is developed in.</summary>
    private static readonly Regex PrivateWorkspaceRef =
        new(@"\b(claudedocs|HANDOFF|dev-docs)\b", RegexOptions.Compiled);

    [Fact]
    public void TheScanReachesAFileThatIsNotAddedYet()
    {
        // Companion to the scan below: the set comes from git, and a guard that saw only tracked
        // files would exempt the file just written until the commit that adds it.
        var probe = Path.Combine(RepoSourceTree.RepoRoot, "tests", "HoneAI.Tests", "Documentation",
            $"NotAddedProbe-{Guid.NewGuid():N}.md");

        try
        {
            File.WriteAllText(probe, "a file the working tree has and the index does not");

            Assert.Contains(probe, RepoSourceTree.PublishedTextFiles());
        }
        finally
        {
            File.Delete(probe);
        }
    }

    [Fact]
    public void TheScanReachesBuildFiles()
    {
        // A comment in a project file is as public as one in a class; the scan must walk them.
        var files = RepoSourceTree.PublishedTextFiles().Select(RepoSourceTree.RelativeToRoot).ToArray();

        Assert.Contains(files, f => f.EndsWith(".csproj", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("Directory.Build.props", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith(".yml", StringComparison.Ordinal));
    }

    [Fact]
    public void PublishedTextCarriesNoInternalToken()
    {
        var findings = new List<string>();

        foreach (var file in RepoSourceTree.PublishedTextFiles())
        {
            var relative = RepoSourceTree.RelativeToRoot(file);
            if (relative.EndsWith(nameof(InternalTokenContractTests) + ".cs", StringComparison.Ordinal))
                continue; // the guard names the shapes it forbids

            var isProse = relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var token in InternalTokensIn(lines[i], isProse))
                    findings.Add($"{relative}:{i + 1}  {token}");
            }
        }

        Assert.True(findings.Count == 0,
            "Published text may not carry tokens that only mean something inside the private " +
            "workspace. Delete a bare cross-reference; replace a marker that ties call sites " +
            "together with a phrase that says what the tie is; rewrite a machine path as a " +
            "placeholder.\n" + string.Join('\n', findings));
    }

    /// <summary>
    /// The guard can still see: each shape it forbids is fed a line containing one, and each
    /// near-miss it must tolerate a line containing that. A pattern that matches nothing passes in
    /// exactly the voice of a clean tree.
    /// </summary>
    [Theory]
    [InlineData("settled as D-18 in review", true, "design-decision id")]
    [InlineData("see BUG-48 for the shape", true, "work-item id with a longer prefix")]
    [InlineData("measured by SEL-01", true, "experiment id")]
    [InlineData("fixed in cycle-93", true, "cycle id")]
    [InlineData("found in the sweep (Cycle 82)", true, "cycle written as a word and a number")]
    [InlineData("found during sprint-35", true, "sprint id")]
    [InlineData(@"open D:\data\project", true, "absolute path on one machine")]
    [InlineData("tracked in claudedocs/plans", true, "private working tree")]
    [InlineData("the design is in dev-docs/HoneAI", true, "private design tree")]
    [InlineData("the next step is in HANDOFF", true, "private working tree")]
    [InlineData("see CVE-2024-1234 and RFC-4180", false, "public identifiers")]
    [InlineData("the SHA-256 of the record", false, "a hash algorithm name")]
    [InlineData("re-encoded as UTF-8", false, "an encoding")]
    [InlineData("SliceKey = \"t-1\", run-1", false, "lower-case fixture data")]
    [InlineData("a two-tier router", false, "an ordinary hyphenated word")]
    public void GuardSeesEachShapeItForbidsAndToleratesEachNearMiss(string line, bool isInternal, string what)
    {
        var found = InternalTokensIn(line, isProse: true).ToArray();

        if (isInternal)
            Assert.True(found.Length > 0, $"the guard no longer recognizes a {what}: \"{line}\"");
        else
            Assert.True(found.Length == 0, $"the guard now flags {what}, which is public vocabulary: {string.Join(", ", found)}");
    }

    private static IEnumerable<string> InternalTokensIn(string line, bool isProse)
    {
        foreach (Match match in WorkItemId.Matches(line))
        {
            if (!PublicPrefixes.Contains(match.Groups["prefix"].Value))
                yield return match.Value;
        }

        if (isProse)
        {
            foreach (Match match in DriveLetterPath.Matches(line))
                yield return match.Value + "…  (absolute path on one machine)";
        }

        foreach (Match match in InternalWorkUnit.Matches(line))
            yield return match.Value + "  (internal unit of work)";

        foreach (Match match in PrivateWorkspaceRef.Matches(line))
            yield return match.Value + "  (private working tree)";
    }
}
