using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Enforces the Phase 0 import boundary (roadmap/13 0-4, §2.1/§6.1): contracts carry
/// zero dependencies, and the dependency direction is one-way (Core → Abstractions,
/// never the reverse), with MLoop consumed over transport rather than by reference.
/// </summary>
/// <remarks>
/// The declared-graph checks parse the <c>.csproj</c> files: an import boundary is about
/// the <em>declared</em> dependency graph the build allows, and that survives the C#
/// compiler eliding references it doesn't yet use (Phase 0 Core barely touches the
/// contracts). The runtime checks complement them by catching transitive pull-in.
/// </remarks>
public class ImportBoundaryTests
{
    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoneAI.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate HoneAI.slnx above the test assembly.");
        return dir!.FullName;
    }

    private static XDocument LoadProject(string relativePath)
        => XDocument.Load(Path.Combine(SolutionRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string[] References(XDocument proj, string element)
        => proj.Descendants(element)
               .Select(e => ((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/'))
               .Where(s => s.Length > 0)
               .ToArray();

    // --- declared dependency graph (the boundary) ---

    [Fact]
    public void Abstractions_DeclaresZeroDependencies()
    {
        // "계약만, 의존 0" — the contract floor every adapter references.
        var proj = LoadProject("src/HoneAI.Abstractions/HoneAI.Abstractions.csproj");
        Assert.Empty(References(proj, "PackageReference"));
        Assert.Empty(References(proj, "ProjectReference"));
    }

    [Fact]
    public void Core_ReferencesAbstractions()
    {
        var proj = LoadProject("src/HoneAI.Core/HoneAI.Core.csproj");
        Assert.Contains(References(proj, "ProjectReference"),
            r => r.EndsWith("HoneAI.Abstractions.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void NoProject_ReferencesMLoopByProjectReference()
    {
        // Phase 0: MLoop is consumed over transport (HTTP/MCP), never duplicated or
        // pulled in by ProjectReference. Catches a declared reference even before any
        // type from it is used.
        foreach (var rel in new[]
                 {
                     "src/HoneAI.Abstractions/HoneAI.Abstractions.csproj",
                     "src/HoneAI.Core/HoneAI.Core.csproj",
                 })
        {
            var mloopRefs = References(LoadProject(rel), "ProjectReference")
                .Where(r => r.Split('/').Any(seg => seg.StartsWith("MLoop", StringComparison.Ordinal)))
                .ToArray();

            Assert.True(mloopRefs.Length == 0,
                $"{rel} must not ProjectReference MLoop in Phase 0: {string.Join(", ", mloopRefs)}");
        }
    }

    // --- runtime complement (transitive pull-in) ---

    private static bool IsFrameworkAssembly(AssemblyName name)
    {
        var n = name.Name ?? string.Empty;
        return n.StartsWith("System", StringComparison.Ordinal)
            || n is "netstandard" or "mscorlib" or "System.Private.CoreLib";
    }

    [Fact]
    public void Abstractions_LoadsNoThirdPartyAssemblies()
    {
        var nonFramework = typeof(PredictionProvenance).Assembly.GetReferencedAssemblies()
            .Where(a => !IsFrameworkAssembly(a))
            .Select(a => a.Name)
            .ToArray();

        Assert.True(nonFramework.Length == 0,
            $"HoneAI.Abstractions must load zero third-party assemblies, found: {string.Join(", ", nonFramework)}");
    }
}
