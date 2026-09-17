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

    [Theory]
    [InlineData("Control")]
    [InlineData("Operator")]
    public void Search_is_referenced_by_nothing_in(string module)
    {
        // Search is the tools and the searches behind them. What repo_info and the operator's pages
        // need from an index — open it, describe it — is the index reader in Infrastructure, so a
        // reference from here into Search is a second reader growing where the first one already is.
        Assert.Empty(ReferencesFrom(module, TypesDeclaredIn("Search")));
    }

    [Theory]
    [InlineData("Search")]
    [InlineData("Refresh")]
    [InlineData("Operator")]
    public void Infrastructure_references_nothing_declared_in(string module)
    {
        // Infrastructure is what every module is handed (ADR-0005, revisited for #35). It may reach
        // Index and Control — the route binding reads the control database, the index reader opens an
        // index — but a tool, a refresh or a web-UI read is a caller of it, never a dependency.
        Assert.Empty(ReferencesFrom("Infrastructure", TypesDeclaredIn(module)));
    }

    [Theory]
    [InlineData("Search")]
    [InlineData("Index")]
    [InlineData("Control")]
    [InlineData("Refresh")]
    [InlineData("Operator")]
    [InlineData("Infrastructure")]
    public void Language_references_nothing_declared_in(string module)
    {
        // Language is the leaf of the graph (ADR-0008). It answers questions about a line of text and
        // knows nothing about indexes, searches or projects, which is what lets a parser-backed
        // analyser be dropped in beside a profile without any of them noticing.
        Assert.Empty(ReferencesFrom("Language", TypesDeclaredIn(module)));
    }

    /// <summary>
    ///     A package boundary rather than a folder one: LibGit2Sharp is how <c>Git/</c> reads a local
    ///     copy, and the refresh and the build are handed what it read as <c>LocalCopy</c> and
    ///     <c>CommittedFile</c>. Naming the namespace anywhere else is the seam turning hypothetical
    ///     again (#37). The test project is not held to this: its fixtures are repositories, and it
    ///     builds them with the same library.
    /// </summary>
    [Fact]
    public void Only_Git_names_LibGit2Sharp()
    {
        var found = new List<string>();
        foreach (string file in SourceTree.ServerFiles())
        {
            if (Path.GetFileName(Path.GetDirectoryName(file)) == "Git") continue;
            if (Regex.IsMatch(SourceTree.Code(file), @"\bLibGit2Sharp\b"))
                found.Add(Path.GetRelativePath(SourceTree.Server(), file));
        }

        Assert.Empty(found);
    }

    /// <summary>The names of the types each <c>.cs</c> file under the module's folder declares.</summary>
    private static HashSet<string> TypesDeclaredIn(string module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(SourceTree.Server(module), "*.cs"))
        foreach (Match match in DeclarationPattern.Matches(SourceTree.Code(file)))
            names.Add(match.Groups[1].Value);
        return names;
    }

    /// <summary>Every "file mentions type" pair from the module to one of the given names.</summary>
    private static List<string> ReferencesFrom(string module, HashSet<string> names)
    {
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(SourceTree.Server(module), "*.cs"))
        {
            string source = SourceTree.Code(file);
            foreach (string name in names)
                if (Regex.IsMatch(source, $@"\b{Regex.Escape(name)}\b"))
                    found.Add($"{Path.GetFileName(file)} references {name}");
        }

        return found;
    }

    // `record struct X` and `record class X` name X, not the second keyword: without the lookahead
    // the first assertion over a module holding one of them reported every `struct` as a reference.
    [GeneratedRegex(@"\b(?:class|record|struct|interface|enum)\s+(?!(?:class|struct)\b)([A-Za-z_]\w*)")]
    private static partial Regex DeclarationPattern { get; }
}
