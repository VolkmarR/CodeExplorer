using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     The one spelling of the comma-separated path terms every filtered surface here accepts:
///     <c>"*.g.cs,/tests/"</c>. A term holding <c>*</c> or <c>?</c> is a SQL <c>GLOB</c> over the whole
///     path, where <c>*</c> crosses <c>/</c> (ADR-0004); anything else is a plain substring. Matching
///     is case-insensitive, which is why every path expression handed in is already lower-cased.
///     It sits in <c>Infrastructure/</c> rather than beside <see cref="FileFilter" />, which is its
///     first caller, because its second is the churn ranking in <see cref="IndexQueries" /> — and
///     <c>Infrastructure/</c> may not reach into <c>Search/</c> (ADR-0005). Two spellings of one filter
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
