using System.Runtime.CompilerServices;
using CodeExplorer.Infrastructure;
using CodeExplorer.Language;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>
///     How a caller's RE2 pattern is run against the lines of an index, for grep and
///     <c>list_matches</c>: what is refused before the index is opened, how RE2's own refusal becomes
///     a sentence, the parameters a single-line match binds, and the recount without file filters.
///     Both searches had a copy of each, word for word. A second copy of the refusals lets two tools
///     explain one malformed pattern two ways, which sends an agent off retrying it on the other, and
///     a second copy of the recount lets "your filters hid it" mean a different thing in each.
///     The searches run their own statements, and the recount here is labelled with its caller's
///     member, so a query plan still names the search that asked (<see cref="QueryPlan" />). The
///     compile alone is the one exception: it is <see cref="Re2.RejectionAsync" />'s statement and is
///     labelled as such, as it was before it was shared.
/// </summary>
internal static class PatternQuery
{
    /// <summary>The test a single-line pattern search filters <c>lines</c> with, over the parameters of <see cref="LineParameters" />.</summary>
    public const string LineMatch = "regexp_matches(l.content, $q, $flags)";

    /// <summary>
    ///     Why this query cannot be run, or null, with <paramref name="pattern" /> set to what is run:
    ///     trimmed, and where it is a pattern, with a <c>\Q</c> it leaves open closed
    ///     (<see cref="Re2.WithQuoteClosed" />). Asked before the index is opened, so a malformed request
    ///     is a sentence and never a miss.
    /// </summary>
    /// <param name="query">The caller's query, untrimmed.</param>
    /// <param name="filter">The request's file filters, whose path terms can be malformed too.</param>
    /// <param name="regex">Whether the query is a pattern; a text query is only checked for being empty.</param>
    /// <param name="empty">The refusal for an empty query, which is the one sentence each tool words its own way.</param>
    /// <param name="pattern">The query as it is to be run.</param>
    public static Problem? Refusal(string query, FileFilter filter, bool regex, string empty, out string pattern)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        pattern = query.Trim();
        if (pattern.Length == 0) return new Problem(empty);
        if (filter.Refusal is { } refused) return new Problem(refused);
        if (!regex) return null;
        if (Re2.Unsupported(pattern) is { } unsupported) return new Problem(unsupported);
        pattern = Re2.WithQuoteClosed(pattern);
        return null;
    }

    /// <summary>
    ///     Runs <paramref name="search" />, and answers RE2 refusing the caller's pattern with a
    ///     <see cref="Problem" /> rather than an exception. Anything else DuckDB raises is infrastructure
    ///     and propagates.
    /// </summary>
    /// <param name="connection">The index's connection, for the compile.</param>
    /// <param name="compileAlone">
    ///     The patterns compiled on their own first, in order, each with <see cref="Re2.RejectionAsync" />;
    ///     the first refused is the answer. Only a pattern search comes through here: a text query hands
    ///     a parser nothing, so a DuckDB error on one is never the caller's to fix.
    ///     The caller's bare pattern leads wherever the search runs a wrapped form of it, because a
    ///     wrapper can balance what the caller left unbalanced. A form the search runs is compiled by the
    ///     search itself, and needs listing only where the search may answer without running it: a
    ///     refusal for size or syntax comes before any other answer (#372).
    /// </param>
    /// <param name="flags">
    ///     The flags the search runs its patterns with, and so the ones they are compiled alone with: the
    ///     flags decide whether one fits RE2's size limit (#364), and a compile that used others would
    ///     refuse a pattern the search runs, or pass one the search then refuses.
    /// </param>
    /// <param name="search">The search, which runs its own statements so their plans carry its name.</param>
    /// <param name="cancellationToken">Threaded to the compile, as every async path here is.</param>
    public static async Task<Outcome> GuardedAsync(DuckDBConnection connection, IReadOnlyList<string> compileAlone,
        string flags, Func<Task<Outcome>> search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compileAlone);
        ArgumentNullException.ThrowIfNull(search);
        try
        {
            foreach (string pattern in compileAlone)
                if (await Re2.RejectionAsync(connection, pattern, flags, cancellationToken) is { } rejection)
                    return new Problem(Re2.Rejected(rejection));
            return await search();
        }
        catch (DuckDBException ex) when (Re2.IsPatternRejection(ex))
        {
            // The pattern is the only caller text a parser sees here; anything else DuckDB raises is
            // infrastructure and propagates.
            return new Problem(Re2.Rejected(ex));
        }
    }

    /// <summary>
    ///     The parameters of <see cref="LineMatch" />: the pattern, anchored on word boundaries when the
    ///     caller asked for whole words, and its case flag.
    /// </summary>
    public static List<DuckDBParameter> LineParameters(string pattern, bool wholeWord, bool caseSensitive) =>
    [
        new("q", LinePattern(pattern, wholeWord)),
        new("flags", LineFlags(caseSensitive))
    ];

    /// <summary>
    ///     The pattern <see cref="LineMatch" /> runs: the caller's, anchored on word boundaries when the
    ///     caller asked for whole words. The one spelling, so a compile alone in <see cref="GuardedAsync" />
    ///     checks the pattern the search runs.
    /// </summary>
    public static string LinePattern(string pattern, bool wholeWord) =>
        wholeWord ? SymbolText.WholeWord(pattern) : pattern;

    /// <summary>
    ///     The RE2 flags of <see cref="LineMatch" />: case-insensitive unless the caller asked otherwise.
    ///     The one spelling, so the compile alone in <see cref="GuardedAsync" /> cannot use other flags
    ///     than the search it guards.
    /// </summary>
    public static string LineFlags(bool caseSensitive) => caseSensitive ? "" : "i";

    /// <summary>
    ///     How many files hold a line <paramref name="match" /> accepts, with no file filter: asked only
    ///     when a filtered search found nothing, so the common case pays nothing, and it is what tells
    ///     "no matches" from "the filters hid them".
    ///     The plan is labelled with the caller's file and member, which are passed on for that.
    /// </summary>
    /// <param name="connection">Already bound to the project being searched.</param>
    /// <param name="match">The search's own line test, against the alias <c>l</c>.</param>
    /// <param name="parameters">The match's parameters and nothing else, so the recount shows its whole input.</param>
    /// <param name="cancellationToken">Threaded to the command, as every async path here is.</param>
    /// <param name="file">The caller's file, filled in by the compiler.</param>
    /// <param name="member">The caller's member, filled in by the compiler.</param>
    public static async Task<int> FilesMatchingAsync(DuckDBConnection connection, string match,
        IEnumerable<DuckDBParameter> parameters, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "") =>
        (int)await connection.CountAsync($"SELECT count(DISTINCT l.file_id) FROM lines l WHERE {match}", parameters,
            cancellationToken, file, member);
}
