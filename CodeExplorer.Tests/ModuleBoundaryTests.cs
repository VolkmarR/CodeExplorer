using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The check ADR-0005 asks for once four modules exist. Everything is in one <c>CodeExplorer</c>
///     namespace, so the compiler enforces nothing and a folder is only a convention; this turns the
///     convention into a failing build.
///     It is not reflection, because reflection cannot see a folder. The declaring folder comes from
///     the source tree and the references from the source text, which is the only place both are
///     visible at once.
/// </summary>
public sealed partial class ModuleBoundaryTests
{
    [Fact]
    public void Search_references_nothing_declared_in_Control()
    {
        var control = TypesDeclaredIn("Control");
        // Search answers from the index alone. Reaching into Control would mean a search opening
        // control.duckdb, which CODING_STANDARDS forbids and which no index-backed answer needs.
        Assert.Empty(ReferencesFrom("Search", control));
    }

    [Fact]
    public void Index_references_nothing_declared_in_Search()
    {
        // The arrow points the other way: Search reads what Index built, and a build knows nothing
        // about how the result will be queried.
        Assert.Empty(ReferencesFrom("Index", TypesDeclaredIn("Search")));
    }

    /// <summary>The names of the types each <c>.cs</c> file under the module's folder declares.</summary>
    private static HashSet<string> TypesDeclaredIn(string module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(ModulePath(module), "*.cs"))
        foreach (Match match in DeclarationPattern.Matches(Strip(File.ReadAllText(file))))
            names.Add(match.Groups[1].Value);
        return names;
    }

    /// <summary>Every "file mentions type" pair from the module to one of the given names.</summary>
    private static List<string> ReferencesFrom(string module, HashSet<string> names)
    {
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(ModulePath(module), "*.cs"))
        {
            string source = Strip(File.ReadAllText(file));
            foreach (string name in names)
                if (Regex.IsMatch(source, $@"\b{Regex.Escape(name)}\b"))
                    found.Add($"{Path.GetFileName(file)} references {name}");
        }

        return found;
    }

    /// <summary>
    ///     Comments and string literals removed, so a type named in a doc comment explaining why a
    ///     module does <em>not</em> use it is not read as a reference to it.
    /// </summary>
    private static string Strip(string source) =>
        Regex.Replace(source, """//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\\n])*"|"{3}[\s\S]*?"{3}""", " ");

    private static string ModulePath(string module) =>
        Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "CodeExplorer", module);

    /// <summary>
    ///     The source tree, not the output directory: the test needs the folders, and the build copies
    ///     assemblies rather than the layout they came from.
    /// </summary>
    private static string ThisFile([CallerFilePath] string path = "") => path;

    [GeneratedRegex(@"\b(?:class|record|struct|interface|enum)\s+([A-Za-z_]\w*)")]
    private static partial Regex DeclarationPattern { get; }
}
