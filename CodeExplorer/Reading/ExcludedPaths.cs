using DuckDB.NET.Data;

namespace CodeExplorer.Reading;

/// <summary>
///     The paths a project's overview page leaves out: the operator's list of globs, such as
///     <c>**/*.verified.txt</c> or <c>**/AssemblyInfo.*</c>, matched against qualified paths. It is a
///     setting of the page and of nothing else — the stored overview <c>project_overview</c> answers
///     from, and every search, are computed without it — because what an operator finds noise in a
///     dashboard is not a fact about the index an agent should be kept from (#216).
///     A pattern means what it would as SQL <c>GLOB</c> (ADR-0004), so <c>*</c> crosses <c>/</c> and
///     <c>**</c> means what <c>*</c> does; it is run as RE2, for the reason <see cref="Matching" />
///     gives. A pattern is anchored at the root of the qualified path — <c>docs/*</c> is
///     the top-level <c>docs</c> and not every folder of that name — unless it begins with a wildcard,
///     and a leading <c>**/</c> also matches at the root, which is what everyone writing one means:
///     <c>**/*.rc</c> drops a root <c>app.rc</c> too. Both follow from matching against the path with
///     a <c>/</c> put in front of it. Case-insensitive, like every other path filter here
///     (<see cref="PathTerms" />).
/// </summary>
public sealed record ExcludedPaths(IReadOnlyList<string> Patterns)
{
    /// <summary>
    ///     How many patterns a project may hold. A list the operator maintains by hand is a screenful at
    ///     most; the ceiling exists so that one pasted file listing cannot become a thousand-term
    ///     disjunction every overview read evaluates against every file.
    /// </summary>
    public const int MaxPatterns = 100;

    /// <summary>
    ///     The longest pattern accepted. A qualified path worth excluding is a few segments deep; past
    ///     this it is a path pasted by mistake rather than a pattern.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>The page with nothing left out, which is also what the "show excluded" switch asks for.</summary>
    public static readonly ExcludedPaths None = new([]);

    /// <summary>Whether anything is left out, asked before the counts of it are paid for.</summary>
    public bool Any => Patterns.Count > 0;

    /// <summary>
    ///     The patterns as they are stored: trimmed, separators forward, blanks and repeats dropped, in
    ///     the order the operator wrote them. The case is kept, because the list is read back into a
    ///     form, and lowered only where it is matched.
    ///     Null with the sentence to show where the list breaks a limit, so an over-long paste is
    ///     refused rather than silently cut to a list the operator did not write.
    /// </summary>
    public static (IReadOnlyList<string>? Patterns, string? Problem) Normalize(IEnumerable<string>? patterns)
    {
        var kept = new List<string>();
        foreach (string raw in patterns ?? [])
        {
            string pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0 || kept.Contains(pattern, StringComparer.OrdinalIgnoreCase)) continue;
            if (pattern.Length > MaxLength)
                return (null, $"A pattern may be at most {MaxLength} characters; '{pattern[..40]}…' is longer.");
            kept.Add(pattern);
        }

        return kept.Count > MaxPatterns
            ? (null, $"A project may exclude at most {MaxPatterns} patterns; this list has {kept.Count}.")
            : (kept, null);
    }

    /// <summary>
    ///     A condition true where <paramref name="path" /> matches any pattern, or null where there
    ///     are none. The callers negate it to leave the paths out and use it as it is to count them.
    ///     One case-insensitive RE2 match of every pattern at once, translated from the globs, rather
    ///     than a GLOB per pattern over a lower-cased path: measured on the Radix index with three
    ///     patterns, 18 ms against 53 ms over <c>files</c> and 48 ms against 177 ms over the commit
    ///     paths, for the same rows. The GLOBs were each a pass over the path and the lower-casing a
    ///     copy of it; one alternation is a single linear pass. The match still runs in DuckDB, where
    ///     CODING_STANDARDS puts every pattern.
    /// </summary>
    /// <param name="path">The SQL naming a qualified path, in any case.</param>
    /// <param name="prefix">Names the bound parameter, so several conditions can share one command.</param>
    /// <param name="parameters">The regex is added here, bound and never inlined.</param>
    public string? Matching(string path, string prefix, List<DuckDBParameter> parameters)
    {
        if (!Any) return null;

        parameters.Add(new DuckDBParameter(prefix, Expression));
        return $"regexp_matches('/' || {path}, ${prefix}, 'i')";
    }

    /// <summary>
    ///     Every pattern as one anchored RE2 alternation: what <see cref="Matching" /> binds, and what
    ///     a save compiles first so that a pattern RE2 refuses is refused there, as a sentence, rather
    ///     than failing every overview read after it (CODING_STANDARDS, Errors).
    /// </summary>
    public string Expression => $"^(?:{string.Join("|", Patterns.Select(AsRegex))})$";

    /// <summary>
    ///     Why <paramref name="pattern" /> cannot be matched, or null where it can. A reversed range such
    ///     as <c>[z-a]</c> is refused by name: GLOB accepts one and matches nothing with it, which in a
    ///     list the operator maintains is a line that silently does nothing. Anything else is asked of the
    ///     engine that will run it, one pattern at a time so the answer can name the one at fault.
    /// </summary>
    public static async Task<string?> RefusedAsync(DuckDBConnection connection, string pattern,
        CancellationToken cancellationToken)
    {
        if (GlobRegex.ReversedRange(pattern) is { } reversed)
            return $"the range {reversed} runs backwards, so no character falls in it.";

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT regexp_matches('', $p, 'i')";
        command.Parameters.Add(new DuckDBParameter("p", new ExcludedPaths([pattern]).Expression));
        try
        {
            await command.ExecuteScalarAsync(cancellationToken);
            return null;
        }
        catch (DuckDBException exception)
        {
            // Swallowed because it is the answer: the engine's own message is what was wrong with the
            // pattern, and each caller acts on it — a save refuses the pattern, a suggestion skips it.
            return exception.Message;
        }
    }

    /// <summary>
    ///     One pattern as the RE2 expression matched against the qualified path with a <c>/</c> in
    ///     front, translated as every glob here is (<see cref="GlobRegex" />).
    ///     The <c>/</c> in front is what anchors: a pattern that does not begin with one or with a
    ///     <c>*</c> is given one, so <c>docs/*</c> is the root's <c>docs</c>, while <c>**/x</c> is left
    ///     free to match the root's own <c>/x</c>. A leading <c>?</c> is anchored like any other
    ///     character, or it would spend itself on that <c>/</c> and match one character short.
    ///     One widening: a <c>/**/</c> between two segments also matches no folder at all, so
    ///     <c>src/**/*.cs</c> reaches <c>src/A.cs</c> the way a leading <c>**/</c> reaches the root. As
    ///     GLOB it could only have matched <c>//</c> there, which no path holds.
    /// </summary>
    internal static string AsRegex(string pattern) =>
        GlobRegex.Translate(pattern.StartsWith('/') || pattern.StartsWith('*') ? pattern : "/" + pattern,
            widenDirectoryStars: true);
}
