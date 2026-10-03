using CodeExplorer.Infrastructure;
using CodeExplorer.Language;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>
///     Which lines name a symbol, for <c>find_references</c> and <c>find_definition</c>: the
///     exact-substring test and the whole-word pattern behind it, and the way into the index both
///     searches take. Both wrote these themselves, the predicate three times over in the definition
///     search, and a symbol the two searches matched differently would be declared on a line that
///     does not use it.
///     The two halves are kept apart because a statement may lay them out on lines of its own;
///     <see cref="Sql" /> is the predicate whole, for every other.
/// </summary>
/// <param name="Literally">
///     The exact-substring test that goes in front of the pattern. A line the pattern matches contains
///     the symbol literally — that is all the pattern is, between word boundaries — so this excludes
///     nothing and DuckDB pushes it into the scan of <c>lines</c>, where the regular expression cannot
///     go. On Radix it halves both symbol searches: <c>find_definition Init</c> 120 ms to 52 ms,
///     <c>find_references SqlSelectBase</c> 110 ms to 42 ms, same rows.
///     Grep has done this since it stopped reading the BM25 index; the two symbol searches asked the
///     engine for a bare pattern until the plans were read (#94), which is the sort of thing only
///     measuring the query finds.
/// </param>
/// <param name="Pattern">The symbol on word boundaries and case-sensitively, which is what decides a match.</param>
internal readonly record struct SymbolMatch(string Literally, string Pattern)
{
    /// <summary>The predicate whole, against the alias <c>l</c>.</summary>
    public string Sql => $"{Literally} AND {Pattern}";

    /// <summary>
    ///     The predicate for <paramref name="symbol" />, with its two parameters, <c>q</c> and
    ///     <c>lit</c>, appended to <paramref name="parameters" />. The names and the column are fixed:
    ///     both callers spell a line's text <c>l.content</c> and bind nothing else under either name.
    /// </summary>
    /// <param name="symbol">The identifier being looked for, exactly as it is matched: case and all.</param>
    /// <param name="parameters">The command's parameters, appended to.</param>
    public static SymbolMatch For(string symbol, List<DuckDBParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Add(new DuckDBParameter("q", SymbolText.WholeWordPattern(symbol)));
        parameters.Add(new DuckDBParameter("lit", symbol));
        return new SymbolMatch("contains(l.content, $lit)", "regexp_matches(l.content, $q, '')");
    }

    /// <summary>
    ///     Refuses a malformed filter or a symbol that is no identifier (<see cref="SearchQuery.Unusable" />),
    ///     then opens the index and hands <paramref name="query" /> the trimmed symbol and the filter
    ///     scoped to the repository the index holds.
    /// </summary>
    /// <param name="readers">Where the index is opened.</param>
    /// <param name="slug">The project.</param>
    /// <param name="symbol">The symbol as the caller sent it.</param>
    /// <param name="filter">The request's file filters.</param>
    /// <param name="tool">The tool to name in a refusal.</param>
    /// <param name="query">The search, which runs its own statements so their plans carry its name.</param>
    /// <param name="cancellationToken">Threaded to the index, as every async path here is.</param>
    public static async Task<Outcome> RunAsync(IndexReaders readers, string slug, string symbol, FileFilter filter,
        string tool, Func<IndexReader, string, FileFilter, CancellationToken, Task<Outcome>> query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(filter);
        string trimmed = symbol.Trim();
        if (filter.Refusal is { } refused) return new Problem(refused);
        if (SearchQuery.Unusable(trimmed, tool) is { } unusable) return new Problem(unusable);

        return await filter.OverIndexAsync(readers, slug,
            (index, resolved, token) => query(index, trimmed, resolved, token), cancellationToken);
    }
}
