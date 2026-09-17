using System.Collections.Frozen;

namespace CodeExplorer;

/// <summary>
///     Which language a file extension belongs to. A project reading "42% .prg, 11% .vh" tells a
///     reader less than "53% X#", and a reader who has never seen <c>.prg</c> learns nothing at all
///     from the first form — so counts are reported by language wherever this knows one.
///     It answers null rather than guessing. An extension nobody declared stands for itself in the
///     answer, which is a weaker statement than a wrong language name and is the same rule the
///     declaration modifiers follow: a form this does not know costs a vaguer answer, a form it
///     invents costs a wrong one reported as right.
///     The registration below is the whole of it, so a reader sees every supported language at once
///     and adding one touches no caller. It is deliberately a table of extensions and nothing more:
///     the rest of what a language is — how it opens a comment, what it calls an assignment, what a
///     declaration looks like — belongs to the language seam #57 builds, which will own this table
///     alongside those facts rather than keeping a second copy of it.
/// </summary>
public static class Languages
{
    /// <summary>
    ///     What an unmapped extension is called where one has to be named, for the files that have no
    ///     extension at all. They are one group and not one per file, and "" would print as nothing.
    /// </summary>
    public const string NoExtension = "(no extension)";

    /// <summary>
    ///     Every language this build knows, and the extensions that mean it. Lowercase and without the
    ///     dot, which is how <c>files.extension</c> is stored.
    ///     Only the languages this server exists to serve are here. A general-purpose list would make
    ///     the grouping look complete when it is not, and an extension left out degrades to standing
    ///     for itself — which is the intended failure.
    /// </summary>
    private static readonly (string Language, string[] Extensions)[] Registered =
    [
        // The dialect this repository's operators mostly read. `.vh` and `.xh` are its headers, which
        // are X# source and are counted as such rather than as a category of their own.
        ("X#", ["prg", "vh", "xh", "ch"]),
        ("C#", ["cs", "csx"]),
        ("TypeScript", ["ts", "tsx", "mts", "cts"]),
        ("JavaScript", ["js", "jsx", "mjs", "cjs"]),
        ("Delphi", ["pas", "dpr", "dpk", "dfm"]),
        ("HTML", ["html", "htm"]),
        ("CSS", ["css"]),
        // `.sql` alone is SQL. PL/SQL is told apart by the package and program-unit extensions, which
        // is the only signal an extension carries: a `.sql` file holding a package body is counted as
        // SQL, because deciding otherwise would mean reading it.
        ("SQL", ["sql"]),
        ("PL/SQL", ["pks", "pkb", "plsql", "prc", "fnc", "trg"])
    ];

    private static readonly FrozenDictionary<string, string> ByExtension =
        Registered
            .SelectMany(entry => entry.Extensions.Select(extension => (extension, entry.Language)))
            .ToFrozenDictionary(pair => pair.extension, pair => pair.Language, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The language this extension means, or null when no profile covers it. The extension is
    ///     spelled the way <c>files.extension</c> stores it — lowercase, no leading dot — and a leading
    ///     dot is tolerated so a caller quoting a file name does not have to strip it.
    /// </summary>
    public static string? Of(string extension) =>
        ByExtension.GetValueOrDefault(extension.TrimStart('.'));

    /// <summary>
    ///     What to call this extension in an answer: its language where one is known, the extension
    ///     itself where none is. The second half of the pair is what lets a reply say which of the two
    ///     it is, because "X#" and ".vh" are claims of different strength and must not read alike.
    /// </summary>
    public static (string Name, bool Mapped) Name(string extension)
    {
        if (Of(extension) is { } language) return (language, true);
        return (extension.Length == 0 ? NoExtension : "." + extension, false);
    }
}
