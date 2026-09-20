using System.Collections.Concurrent;
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
///     It runs over every ordered pair of modules rather than over six hand-picked facts, so the
///     default for a new arrow is to fail: a pair absent from <see cref="Allowed" /> is a boundary
///     crossing nobody decided on. The hand-picked version could only ever catch the crossings someone
///     had thought to write a <c>[Fact]</c> for, which is how <c>Control/</c> came to reference
///     <c>Index/</c> and close a cycle with the <c>Index/</c> → <c>Control/</c> arrow the ADR allows.
/// </summary>
public sealed partial class ModuleBoundaryTests
{
    /// <summary>
    ///     One folder per module (ADR-0005), read off the source tree rather than listed here.
    ///     A list would have to be edited before a new module was swept at all, which is the same hole
    ///     in a different place: the arrows into and out of it would be unguarded until someone
    ///     remembered. Reading the folders means a new one starts with no allowed arrows, so its first
    ///     reference in either direction fails and has to be argued for.
    ///     A module folder is one holding <c>.cs</c> files of its own, which is the rule rather than a
    ///     list of names to keep out: <c>wwwroot</c> is a Vite build and <c>data</c> is whatever a
    ///     <c>dotnet run</c> left on this machine, so a name list would have made the sweep depend on
    ///     who had run the server. <c>bin</c> and <c>obj</c> do hold generated <c>.cs</c> and are named,
    ///     for the reason <see cref="SourceTree.ServerFiles" /> names them: a rule about what this code
    ///     says is not about those. <c>Program.cs</c> is at the root and is in no module.
    /// </summary>
    private static readonly string[] Modules =
        Directory.EnumerateDirectories(SourceTree.Server())
            .Where(folder => Directory.EnumerateFiles(folder, "*.cs").Any())
            .Select(folder => Path.GetFileName(folder)!)
            .Where(name => name is not ("bin" or "obj"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     ADR-0005's arrows, as <c>from → to</c>. Every other ordered pair is a failing build, so this
    ///     list and the ADR's prose are the same statement and have to be changed together.
    ///     <list type="bullet">
    ///         <item>
    ///             <c>Control/</c> is the root the arrows point away from: it owns projects, repositories
    ///             and credentials, and reaches only <c>Infrastructure/</c>.
    ///         </item>
    ///         <item>
    ///             <c>Git/</c> reaches nothing but <c>Infrastructure/</c> either, and the two no longer
    ///             touch at all. <c>Git/</c> read the repository record and the credential purpose out of
    ///             <c>Control/</c> while <c>Control/</c> read <c>Git/</c>'s URL classifier; the first two
    ///             moved to <c>Infrastructure/</c> and the classifier moved to <c>Control/</c>, whose
    ///             validation has been its only reader since ADR-0007 made every clone full.
    ///         </item>
    ///         <item>
    ///             <c>Index/</c> builds, so it reaches what a build reads: <c>Git/</c> for the local copy
    ///             and <c>Language/</c> for what a line means. Not <c>Control/</c> any more — the one
    ///             thing it read there was the repository record, which now sits in
    ///             <c>Infrastructure/</c> with the project record it belongs beside.
    ///         </item>
    ///         <item>
    ///             <c>Search/</c> answers from an index alone, so it reaches only
    ///             <c>Infrastructure/</c> and <c>Language/</c>. Not <c>Index/</c> — an index is opened
    ///             through <c>IndexReaders</c> — not <c>Git/</c>, because an answer comes from what the
    ///             last build read and never from a remote, and not <c>Control/</c>, because a search
    ///             opening <c>control.duckdb</c> is what CODING_STANDARDS forbids.
    ///         </item>
    ///         <item>
    ///             <c>Infrastructure/</c> is what every module is handed, so nothing it holds may reach a
    ///             caller of it. Its two arrows are the route binding, which reads the control database,
    ///             and <c>IndexReaders</c>, which opens an index.
    ///         </item>
    ///         <item>
    ///             <c>Language/</c> is the leaf (ADR-0008) and reaches nothing. Being the leaf is what
    ///             lets anything reach it, which is why <c>Infrastructure/</c> may: the spelling of an
    ///             <c>imports</c> column is a language fact on one side and a stored string on the other.
    ///         </item>
    ///     </list>
    /// </summary>
    private static readonly HashSet<string> Allowed =
    [
        "Control -> Infrastructure",
        "Git -> Infrastructure",
        "Index -> Git",
        "Index -> Infrastructure",
        "Index -> Language",
        "Infrastructure -> Control",
        "Infrastructure -> Index",
        "Infrastructure -> Language",
        "Operator -> Control",
        "Operator -> Git",
        "Operator -> Infrastructure",
        "Refresh -> Control",
        "Refresh -> Git",
        "Refresh -> Index",
        "Refresh -> Infrastructure",
        "Search -> Infrastructure",
        "Search -> Language"
    ];

    public static TheoryData<string, string> ModulePairs
    {
        get
        {
            var pairs = new TheoryData<string, string>();
            foreach (string from in Modules)
            foreach (string to in Modules)
                if (from != to)
                    pairs.Add(from, to);
            return pairs;
        }
    }

    [Theory]
    [MemberData(nameof(ModulePairs))]
    public void A_module_references_only_the_modules_ADR_0005_allows(string from, string to)
    {
        var found = ReferencesFrom(from, TypesDeclaredIn(to));
        if (Allowed.Contains($"{from} -> {to}"))
        {
            // An allowed arrow nobody draws is an entry that has outlived its reason and silently
            // re-grants itself the day someone draws it again. The list is meant to be the ADR's prose
            // in code, so an arrow that is gone has to be struck from both.
            Assert.True(found.Count > 0,
                $"Nothing in {from}/ references {to}/ any more. Remove \"{from} -> {to}\" from Allowed and "
                + "from ADR-0005, or restore the reference.");
            return;
        }

        Assert.True(found.Count == 0,
            $"{from}/ must not reference {to}/ (ADR-0005). Either move the type or add \"{from} -> {to}\" to "
            + $"Allowed and say why in the ADR.{Environment.NewLine}{string.Join(Environment.NewLine, found)}");
    }

    /// <summary>
    ///     The sweep above reports nothing, which is also what a broken detector reports. These are the
    ///     positions it has to see and the ones it has to leave alone, asserted against text rather than
    ///     against the tree, so a regex that stops matching fails here instead of passing silently.
    /// </summary>
    [Theory]
    // Seen: construction, a declaration, a generic argument, a base list, a static member, a cast.
    [InlineData("var x = new Refused(why);", true)]
    [InlineData("void Take(Refused refusal) { }", true)]
    [InlineData("IReadOnlyList<Refused> All => [];", true)]
    [InlineData("sealed record Worse(string Why) : Refused(Why);", true)]
    [InlineData("string rule = Refused.Rule;", true)]
    [InlineData("var r = (Refused)outcome;", true)]
    // Left alone: a member read, a method of that name, a longer name that starts with it, a word.
    [InlineData("return copy.Refused;", false)]
    [InlineData("static string Refused(string why) => why;", false)]
    [InlineData("var x = new RefusedCopy(why);", false)]
    [InlineData("_ = outcome.Kind == Kind.RefusedByLfs;", false)]
    public void A_name_counts_as_a_reference_only_where_C_sharp_puts_a_type(string line, bool isAReference) =>
        Assert.Equal(isAReference, TypePosition("Refused").IsMatch(line));

    /// <summary>
    ///     The test project mirrors the host's folders (CODING_STANDARDS, Layout), so a module's tests
    ///     are where the module is and a reader looking for them has one place to look. A folder here
    ///     naming no module is a stray; the files at this project's root are the harness the whole
    ///     suite shares and are in no module, as <c>Program.cs</c> is.
    /// </summary>
    [Fact]
    public void The_test_project_mirrors_the_module_folders() =>
        Assert.Equal(Modules, TestFolders());

    /// <summary>
    ///     Every folder of this project holding tests, as a path relative to its root: relative and not
    ///     just the name, so a folder nested inside a module's reads as the stray it is instead of
    ///     passing for its parent. <c>bin</c> and <c>obj</c> are left out wherever they appear, for the
    ///     reason <see cref="SourceTree.ServerFiles" /> leaves them out of the server's own.
    /// </summary>
    private static IEnumerable<string> TestFolders() =>
        Directory.EnumerateDirectories(SourceTree.Tests(), "*", SearchOption.AllDirectories)
            .Where(folder => Directory.EnumerateFiles(folder, "*.cs").Any())
            .Select(folder => Path.GetRelativePath(SourceTree.Tests(), folder))
            .Where(relative => !relative.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .OrderBy(relative => relative, StringComparer.Ordinal);

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

    /// <summary>
    ///     The top-level types each <c>.cs</c> file under the module's folder declares. Nested types are
    ///     left out on purpose: no other module can name one without naming its enclosing type first, so
    ///     the enclosing type is the reference, and it is the nested ones that carry the common nouns —
    ///     <c>Refused</c>, <c>Empty</c>, <c>Opened</c> — that a pairwise sweep would otherwise report
    ///     from every file that happens to use the word.
    ///     A top-level declaration is one that starts at column 0, which is what the formatter guarantees
    ///     for a file in the single <c>CodeExplorer</c> namespace and what no nested declaration can look
    ///     like.
    /// </summary>
    private static HashSet<string> TypesDeclaredIn(string module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(SourceTree.Server(module), "*.cs"))
        foreach (Match match in DeclarationPattern.Matches(SourceTree.Code(file)))
            names.Add(match.Groups[1].Value);
        return names;
    }

    /// <summary>
    ///     Every "file names type in a type position" pair from the module to one of the given names.
    ///     A name the file declares itself, at any nesting, is its own and is skipped: a nested
    ///     <c>Refused</c> is written bare inside the file that declares it, which is indistinguishable
    ///     from a reference to a <c>Refused</c> somewhere else by looking at the text alone. C# resolves
    ///     the inner one, so the file is skipped for that name rather than reported for it.
    /// </summary>
    private static List<string> ReferencesFrom(string module, HashSet<string> names)
    {
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(SourceTree.Server(module), "*.cs"))
        {
            string source = SourceTree.Code(file);
            var own = DeclaredAnywherePattern.Matches(source)
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (string name in names)
                if (!own.Contains(name) && TypePosition(name).IsMatch(source))
                    found.Add($"  {Path.GetFileName(file)} references {name}");
        }

        return found;
    }

    /// <summary>
    ///     Where a name means the type rather than a member, a parameter or an English word. The bare
    ///     <c>\bName\b</c> the first version of this test used was fine over six curated pairs and is not
    ///     over all fifty-six: <c>Reference</c>, <c>Answer</c>, <c>Declared</c> and <c>Hole</c> are all
    ///     real top-level types here and all also ordinary property and variable names elsewhere, so the
    ///     sweep reported arrows nobody had drawn.
    ///     The alternation is the positions C# actually puts a type in. Every branch refuses a leading
    ///     <c>.</c>, so <c>copy.Reference</c> is a member read and not a mention of <c>Reference</c>.
    /// </summary>
    // One regex per name, built once: the sweep asks the same few hundred names of every file of every
    // module, and rebuilding each pattern per file is the whole cost of the run.
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    private static Regex TypePosition(string name) => Patterns.GetOrAdd(name, Build);

    private static Regex Build(string name)
    {
        string n = $@"(?<![\w.]){Regex.Escape(name)}\b";
        return new Regex(string.Join("|",
            // new Type(…), new Type { … }, new Type[…]
            $@"\bnew\s+{n}",
            // a generic argument, or one element of a list of them
            $@"[<(,]\s*{n}\s*(?=[>,\]])",
            // a base list, a type constraint, or the type side of a ternary or a named argument
            $@":\s*{n}(?=[\s<({{;,])",
            // a declaration: a parameter, a field, a local, a record component
            $@"{n}\s*\??\s*(?:\[\])?\s+@?[A-Za-z_]\w*",
            // a static member, a constant, an enum value
            $@"{n}\s*\.",
            $@"\btypeof\(\s*{n}",
            $@"\b(?:is|as)\s+{n}",
            // a cast, which is a type between parentheses with an expression after it
            $@"\(\s*{n}\s*\)\s*(?=[@\w""(])"));
    }

    // Anchored at column 0 so only top-level declarations count, and spelling `record class` and
    // `record struct` out because the name is the third word there and the second everywhere else:
    // without that, the first assertion over a module holding one of them read every `struct` as a
    // type called "struct".
    [GeneratedRegex(
        @"^(?:(?:public|internal|file|sealed|abstract|static|partial|readonly|unsafe)\s+)*"
        + @"(?:class|interface|enum|struct|record(?:\s+(?:class|struct))?)\s+([A-Za-z_]\w*)",
        RegexOptions.Multiline)]
    private static partial Regex DeclarationPattern { get; }

    // The same at any nesting and any indent, for "does this file declare the name itself".
    [GeneratedRegex(@"\b(?:class|interface|enum|struct|record(?:\s+(?:class|struct))?)\s+([A-Za-z_]\w*)")]
    private static partial Regex DeclaredAnywherePattern { get; }
}
