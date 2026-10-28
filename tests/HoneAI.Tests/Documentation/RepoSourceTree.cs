using System.Diagnostics;

namespace HoneAI.Tests.Documentation;

/// <summary>
/// Locating and reading this repository's published text — what a source-text guard needs and should
/// not own a copy of.
/// </summary>
internal static class RepoSourceTree
{
    /// <summary>The file extensions whose text this repository publishes.</summary>
    /// <remarks>
    /// Build files are on the list as well as sources and documents: a comment in a project file is
    /// as public as one in a class, and it is the one nobody rereads.
    /// </remarks>
    internal static readonly string[] PublishedPatterns = ["*.md", "*.cs", "*.csproj", "*.props", "*.yml", "*.sh", "*.ps1", ".gitignore"];

    /// <summary>The repository root, found by walking up from the build output.</summary>
    internal static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>
    /// Every file of <see cref="PublishedPatterns"/> this repository publishes, as absolute paths.
    /// </summary>
    /// <remarks>
    /// "Publishes" is asked of git rather than of the file system: the working tree also holds
    /// ignored tooling state no reader ever sees. A file that is not yet added counts as published —
    /// asking only for tracked files would hide the one most likely to carry a fresh violation, the
    /// one just written, until the commit that adds it.
    /// </remarks>
    internal static IEnumerable<string> PublishedTextFiles()
    {
        var git = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-C", RepoRoot, "ls-files", "--cached", "--others", "--exclude-standard", "--" })
            git.ArgumentList.Add(argument);
        foreach (var pattern in PublishedPatterns)
            git.ArgumentList.Add(pattern);

        using var process = Process.Start(git)
            ?? throw new InvalidOperationException(
                "Could not run git to list tracked files — this guard asks git what the repository publishes.");

        var files = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            files.Add(Path.Combine(RepoRoot, line.Replace('/', Path.DirectorySeparatorChar)));
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git ls-files failed with exit code {process.ExitCode}.");

        return files.Where(File.Exists);
    }

    /// <summary>The path of <paramref name="file"/> relative to <see cref="RepoRoot"/>.</summary>
    internal static string RelativeToRoot(string file) => Path.GetRelativePath(RepoRoot, file);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HoneAI.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not locate HoneAI.slnx by walking up from {AppContext.BaseDirectory} — " +
            $"{nameof(RepoSourceTree)} assumes it runs from within the repo's build output tree.");
    }
}
