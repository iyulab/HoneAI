using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace HoneAI.Tests.Documentation;

/// <summary>
/// The C# examples in README.md call only names the library has: every PascalCase name and every named
/// argument in a <c>csharp</c> block is a public type, member or parameter of the HoneAI assemblies.
/// </summary>
/// <remarks>
/// <para>
/// The README's blocks use variables they never define (<c>mloop</c>, <c>training</c>, <c>line</c>), so they cannot be
/// compiled as written, and compiling them would take a scaffold and a compiler dependency in the tests. What a
/// reader trips on first is the simpler thing — a type or a property the example names that the package does not
/// have, after a rename. That is checked here by reflection: the names of the public surface are collected once and
/// each name the examples use must be among them. It does not check signatures, argument counts or that a name sits
/// on the type the example puts it on, and it cannot see a local variable (those start in lower case).
/// </para>
/// <para>
/// What the examples define themselves (the stand-ins for the reader's own code) and what the framework provides are
/// listed below, each with a reason, so the list is short enough to read. A name added to it should be a stand-in or
/// a framework name, never a HoneAI name — those belong in the surface.
/// </para>
/// </remarks>
public class ReadmeExampleNamesContractTests
{
    /// <summary>Names the examples use that are the reader's own code or the framework's, not this library's.</summary>
    private static readonly HashSet<string> NotThisLibrary = new(StringComparer.Ordinal)
    {
        "ClassifyWithModelAsync", "ClassifyWithLlmAsync", "AskAPersonAsync",   // the reader's judges
        "LogLine", "TemplateKey",                                               // the reader's input type and its property
        "Console", "WriteLine", "StringReader",                                 // the framework
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "using", "var", "new", "await", "async", "foreach", "in", "string", "null", "true", "false", "this", "is", "not",
        "int", "long", "double", "bool", "object", "return", "if", "else",
    };

    /// <summary>Every public type, member and parameter name of the HoneAI assemblies (generic arity removed).</summary>
    internal static HashSet<string> PublicSurfaceNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // Touch one type of each assembly so it is loaded, then read what is loaded.
        _ = typeof(HoneAI.Tier).Assembly;
        _ = typeof(HoneAI.Routing.TieredRouter<,>).Assembly;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is { } n && n.StartsWith("HoneAI", StringComparison.Ordinal) && n != "HoneAI.Tests"))
        {
            foreach (var type in assembly.GetExportedTypes())
                Collect(type, names);
        }
        return names;

        static void Collect(Type type, HashSet<string> names)
        {
            names.Add(StripArity(type.Name));
            foreach (var segment in (type.Namespace ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
                names.Add(segment);   // `using HoneAI.Routing;` names namespaces
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(all))
            {
                if (member is Type nested)
                {
                    if (nested.IsNestedPublic)
                        Collect(nested, names);
                    continue;
                }
                names.Add(member.Name);
                if (member is MethodBase method)
                    foreach (var parameter in method.GetParameters())
                        if (parameter.Name is { } p)
                            names.Add(p);
            }
        }

        static string StripArity(string name) => name.Contains('`') ? name[..name.IndexOf('`')] : name;
    }

    /// <summary>
    /// The names a README's <c>csharp</c> blocks use, without what the reader reads past: comments and the text of string
    /// literals (the holes of an interpolated string are code and stay). Returns the PascalCase names and the named arguments.
    /// </summary>
    internal static (HashSet<string> Names, HashSet<string> Arguments) NamesUsedByExamples(string markdown)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var arguments = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in Regex.Matches(markdown, @"```csharp\r?\n(.*?)```", RegexOptions.Singleline))
        {
            var code = Regex.Replace(block.Groups[1].Value, @"//[^\r\n]*", "");
            code = Regex.Replace(code, @"\$""((?:[^""\\]|\\.)*)""", m => string.Concat(Regex.Matches(m.Groups[1].Value, @"\{[^}]*\}").Select(h => " " + h.Value + " ")));
            code = Regex.Replace(code, @"""(?:[^""\\]|\\.)*""", " ");
            foreach (Match token in Regex.Matches(code, @"(?<![\w.@])@?([A-Za-z_][A-Za-z0-9_]*)\b(\s*:(?!:))?"))
            {
                var name = token.Groups[1].Value;
                if (Keywords.Contains(name))
                    continue;
                if (token.Groups[2].Success)
                    arguments.Add(name);
                else if (char.IsUpper(name[0]))
                    names.Add(name);
            }
            // Names after a dot or inside a member chain (record.Provenance.Kind) are members the example touches.
            foreach (Match member in Regex.Matches(code, @"\.([A-Z][A-Za-z0-9_]*)\b"))
                names.Add(member.Groups[1].Value);
        }
        return (names, arguments);
    }

    [Fact]
    public void EveryNameTheReadmeExamplesUseIsOnTheLibrarysPublicSurface()
    {
        var readme = File.ReadAllText(Path.Combine(RepoSourceTree.RepoRoot, "README.md"));
        var surface = PublicSurfaceNames();
        var (names, arguments) = NamesUsedByExamples(readme);

        // The scan reaches a real surface and real examples: a type and a named argument the examples depend on are in both.
        Assert.Contains("TieredRouter", surface);
        Assert.Contains("TieredRouter", names);
        Assert.Contains("decisionIdOf", arguments);
        Assert.Contains("decisionIdOf", surface);

        var missingNames = names.Except(surface).Except(NotThisLibrary).Order(StringComparer.Ordinal).ToList();
        var missingArguments = arguments.Except(surface).Order(StringComparer.Ordinal).ToList();

        Assert.True(missingNames.Count == 0 && missingArguments.Count == 0,
            "The README examples use names the library does not have (renamed or removed?). Fix the example, or — for the reader's own code — list the name in "
            + nameof(NotThisLibrary) + ":\nnames: " + string.Join(", ", missingNames) + "\narguments: " + string.Join(", ", missingArguments));
    }

    /// <summary>The scan sees what it must: names and named arguments are read, a comment and a string's text are not, an interpolation hole is.</summary>
    [Fact]
    public void TheScanReadsCodeAndSkipsCommentsAndStrings()
    {
        var markdown = "text `Ignored`\n```csharp\nvar a = new RealThing(name: 1); // MentionedInAComment\nvar s = \"TextOfAString\";\nvar t = $\"{x.InsideAHole}\";\n```\n```bash\nNotCSharp\n```\n";

        var (names, arguments) = NamesUsedByExamples(markdown);

        Assert.Contains("RealThing", names);
        Assert.Contains("name", arguments);
        Assert.Contains("InsideAHole", names);
        Assert.DoesNotContain("MentionedInAComment", names);
        Assert.DoesNotContain("TextOfAString", names);
        Assert.DoesNotContain("NotCSharp", names);
        Assert.DoesNotContain("Ignored", names);

        // And a name the library lacks is reported.
        Assert.DoesNotContain("RealThing", PublicSurfaceNames());
    }
}
