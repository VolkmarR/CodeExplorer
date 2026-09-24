using DuckDB.NET.Data;

namespace CodeExplorer.Reading;

/// <summary>
///     How a churn ranking is narrowed past its window and its scope: <see cref="Extensions" /> is the
///     only extensions to rank and <see cref="Exclude" /> the paths to drop, both in the
///     <see cref="PathTerms" /> syntax. One record rather than two arguments threaded side by side,
///     because the file ranking, the directory rollup and the count of what they hid all have to
///     narrow by exactly the same thing — a rollup filtered differently from the files under it is a
///     directory whose churn nothing on screen adds up to.
///     Both null is the unfiltered ranking, which is what every surface asked for before #161.
///     <see cref="Excluded" /> is the overview page's own setting, in its own glob syntax (#216), and
///     is carried here rather than beside the record so that the page's ranking and its count of what
///     was left out narrow by one thing, the way the two filters above do.
/// </summary>
public sealed record ChurnFilters(string? Extensions = null, string? Exclude = null, ExcludedPaths? Excluded = null)
{
    /// <summary>The ranking nothing was asked to leave out, named so a call site reads as one.</summary>
    public static readonly ChurnFilters None = new();

    /// <summary>
    ///     Whether anything is filtered at all. Asked before the count of what was hidden is run,
    ///     because that count is a second scan of the window and an unfiltered call must not pay for it.
    /// </summary>
    public bool Any =>
        PathTerms.Split(Extensions).Count > 0 || PathTerms.Split(Exclude).Count > 0 || Excluded?.Any == true;
}

/// <summary>
///     The one spelling of the comma-separated path terms every filtered surface here accepts:
///     <c>"*.g.cs,/tests/"</c>. A term holding <c>*</c> or <c>?</c> is a SQL <c>GLOB</c> over the whole
///     path, where <c>*</c> crosses <c>/</c> (ADR-0004); anything else is a plain substring. Matching
///     is case-insensitive, which is why every path expression handed in is already lower-cased.
///     It sits in <c>Reading/</c> rather than beside <see cref="CodeExplorer.Search.FileFilter" />, which is its
///     first caller, because its second is the churn ranking in <see cref="IndexQueries" /> — and
///     <c>Reading/</c> may not reach into <c>Search/</c> (ADR-0005). Two spellings of one filter
///     syntax is the drift an agent cannot see: <c>exclude="*.g.cs"</c> answered one way by grep and
///     another by hot_files is worse than hot_files not taking the argument at all.
/// </summary>
public static class PathTerms
{
    /// <summary>
    ///     The terms of one argument, normalised: separators forward, case down, blanks dropped. An
    ///     empty or absent argument is no terms, which is no filtering rather than a filter matching
    ///     everything.
    /// </summary>
    public static List<string> Split(string? terms) =>
    [
        .. (terms ?? "")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.Replace('\\', '/').ToLowerInvariant())
    ];

    /// <summary>
    ///     One term as SQL over <paramref name="path" />, an expression that already lower-cases the
    ///     path it reads.
    /// </summary>
    /// <param name="term">A term from <see cref="Split" />, bound as <paramref name="parameter" />.</param>
    /// <param name="path">The SQL naming the path to match, e.g. <c>lower(f.qualified_path)</c>.</param>
    /// <param name="parameter">The bound parameter holding the term; values are never inlined.</param>
    public static string Match(string term, string path, string parameter) =>
        term.Contains('*', StringComparison.Ordinal) || term.Contains('?', StringComparison.Ordinal)
            ? $"{path} GLOB {parameter}"
            : $"contains({path}, {parameter})";

    /// <summary>
    ///     The terms of an <c>extensions</c> argument as a disjunction — a path is kept where it ends
    ///     in any of them — or null where the argument filters nothing. The mirror of
    ///     <see cref="Excluding" /> and the opposite polarity: <c>exclude</c> says what to drop and
    ///     this says the only thing to keep, so the two compose as "of these extensions, not these
    ///     paths" rather than fighting.
    ///     A term is spelled as the extension a reader sees — <c>.cs</c>, or <c>cs</c>, or the whole
    ///     <c>*.cs</c> — and becomes a GLOB anchored at the end of the path, which is what keeps
    ///     <c>.cs</c> from also selecting <c>.cshtml</c> the way a substring would. A term the caller
    ///     already wrote as a pattern is left as written, so <c>*.g.cs</c> asks what it looks like it
    ///     asks.
    /// </summary>
    /// <param name="extensions">The caller's argument, comma-separated like every other one here.</param>
    /// <param name="path">The SQL naming the path to match, lower-cased by the caller.</param>
    /// <param name="prefix">Names the bound parameters, so several filters can share one command.</param>
    /// <param name="parameters">Each term is added here, bound and never inlined.</param>
    public static string? Including(string? extensions, string path, string prefix,
        List<DuckDBParameter> parameters)
    {
        var terms = Split(extensions);
        if (terms.Count == 0) return null;

        var conditions = new List<string>(terms.Count);
        for (int i = 0; i < terms.Count; i++)
        {
            string glob = AsExtensionGlob(terms[i]);
            conditions.Add(Match(glob, path, $"${prefix}{i}"));
            parameters.Add(new DuckDBParameter($"{prefix}{i}", glob));
        }

        return string.Join(" OR ", conditions);
    }

    /// <summary>
    ///     One extension term as the pattern it means. Three spellings reach the same place because
    ///     all three are what someone types: the UI sends what it read off a path, and a person
    ///     writes whichever of the two shorter ones they think in.
    /// </summary>
    private static string AsExtensionGlob(string term) =>
        term.Contains('*', StringComparison.Ordinal) || term.Contains('?', StringComparison.Ordinal)
            ? term
            : term.StartsWith('.')
                ? $"*{term}"
                : $"*.{term}";

    /// <summary>
    ///     The terms of an <c>exclude</c> argument as a conjunction of negations — a path is kept only
    ///     where it matches none of them — or null where the argument filters nothing.
    /// </summary>
    /// <param name="exclude">The caller's argument, in the syntax this class defines.</param>
    /// <param name="path">The SQL naming the path to match, lower-cased by the caller.</param>
    /// <param name="prefix">Names the bound parameters, so several filters can share one command.</param>
    /// <param name="parameters">Each term is added here, bound and never inlined.</param>
    public static string? Excluding(string? exclude, string path, string prefix,
        List<DuckDBParameter> parameters)
    {
        var terms = Split(exclude);
        if (terms.Count == 0) return null;

        var conditions = new List<string>(terms.Count);
        for (int i = 0; i < terms.Count; i++)
        {
            conditions.Add($"NOT ({Match(terms[i], path, $"${prefix}{i}")})");
            parameters.Add(new DuckDBParameter($"{prefix}{i}", terms[i]));
        }

        return string.Join(" AND ", conditions);
    }
}
