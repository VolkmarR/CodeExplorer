using System.Globalization;
using CodeExplorer.Infrastructure;
using CodeExplorer.Language;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>Everything a grep call asks for. Bounds are enforced by <see cref="GrepSearch" />, not by the caller.</summary>
public sealed record GrepRequest(
    string Query,
    bool Regex = false,
    bool CaseSensitive = false,
    string? Path = null,
    string? Exclude = null,
    string? Extension = null,
    bool Multiline = false,
    bool WholeWord = false,
    int Context = 0,
    bool FilesOnly = false,
    int MaxLinesPerFile = 20,
    int Page = 1,
    int PageSize = 20,
    bool WithHistory = false)
{
    /// <summary>
    ///     The three filters as the shape every index search shares. Grep spans every repository in
    ///     the project by design, so it never sets one, and its tool description promises as much.
    /// </summary>
    internal FileFilter Filter => new(null, Path, Exclude, Extension);

    public bool HasFileFilters => Filter.Any;
}

/// <summary>
///     One line of an answer. <see cref="IsMatch" /> is false for a line returned only as context around
///     a match. <see cref="By" /> is filled only when the caller asked for history and the
///     build attributed the line; it says who last changed it, never who wrote it (CONTEXT.md,
///     Attribution).
/// </summary>
public sealed record GrepLine(int LineNumber, string Text, bool IsMatch, AttributedBy? By = null);

/// <summary>
///     One file's share of the answer. <see cref="MatchesShown" /> is how many of the
///     <see cref="MatchCount" /> matches <see cref="Lines" /> covers; in multiline mode one match can
///     span several lines, so it is not the number of marked lines. Both are zero-lines for a files-only search.
///     <see cref="Unread" /> is a multiline file the page counted but did not read, because the files
///     before it spent <see cref="GrepSearch.MaxMultilinePageBytes" />: it has no lines, and why is not
///     the file's.
/// </summary>
public sealed record GrepFile(
    string QualifiedPath,
    int MatchCount,
    int MatchesShown,
    IReadOnlyList<GrepLine> Lines,
    bool Unread = false);

/// <summary>
///     A page of matches; the answer when the search was not a <see cref="Problem" />.
///     <see cref="Engine" /> names what answered, because full-text and substring
///     scan rank differently. <see cref="FilesMatchingWithoutFilters" /> is filled only when nothing
///     matched under path, extension or exclude filters: it tells "no matches" from "matches existed
///     and the filters hid them", which read identically and mean opposite things.
///     <see cref="FilesNotSearched" /> is how many candidate files a multiline count left out once it
///     had read <see cref="GrepSearch.MaxMultilineCountBytes" />: where it is not zero the totals are a
///     lower bound. <see cref="FilesNotSearchedOutsideFilters" /> is the same for the recount without
///     filters, set only when that recount found nothing in what it read, so whether the pattern
///     matches outside the filters is not known and <see cref="FilesMatchingWithoutFilters" /> is null.
/// </summary>
public sealed record GrepResult(
    string Engine,
    int TotalFiles,
    long TotalLines,
    int Page,
    int PageSize,
    IReadOnlyList<GrepFile> Files,
    int? FilesMatchingWithoutFilters,
    int FilesNotSearched = 0,
    int FilesNotSearchedOutsideFilters = 0) : Outcome;

/// <summary>
///     Text and regular-expression search over a project's <c>lines</c>. All matching runs inside
///     DuckDB (ADR-0004): a text query matches each identifier piece as a whole token and then
///     verifies it with <c>contains</c> so the answer is exact; regex is RE2 through
///     <c>regexp_matches</c>. The substring fallback for text queries is <c>contains</c> rather than
///     the <c>regexp_matches</c> ADR-0004 names, because a text query is not a pattern and escaping it
///     into one buys nothing. Nothing here opens <c>control.duckdb</c> (ADR-0005).
///     The whole-token half was BM25 over the full-text index until the plan showed it narrowed
///     nothing: the exact-verify was pushed into a scan of every line either way, so reading the index
///     was pure cost — 2,226 ms of CPU and a 32-million-row scan of its own term table on a
///     9.5-million-line project. The <c>\b…\b</c> tests here are the same rule against the same
///     tokeniser, which is why the answers are identical and the query is two to three times faster.
/// </summary>
public sealed partial class GrepSearch(IndexReaders readers)
{
    /// <summary>
    ///     What a text query is answered by when the project's lines were tokenised: every identifier
    ///     piece of the query matched as a whole token, then verified with <c>contains</c>. Named for
    ///     what it does rather than for the index it used to read, because it no longer reads one.
    /// </summary>
    public const string TokenEngine = "token scan";
    public const string SubstringEngine = "substring scan";
    public const string RegexEngine = "regex scan";
    public const string MultilineEngine = "multiline regex scan";

    /// <summary>Ten lines each way is a whole method around a hit; beyond that read_file is the right tool.</summary>
    public const int MaxContext = 10;

    /// <summary>A file that matches more than this is telling the agent to narrow the query, not to read on.</summary>
    public const int MaxLinesPerFile = 200;

    /// <summary>
    ///     A hundred files at the default twenty lines each is already past the reply cap; a larger
    ///     page would only be truncated, so paging is the honest way to see more.
    /// </summary>
    public const int MaxPageSize = 100;

    /// <summary>
    ///     The most file content one multiline page reads to mark its matches. Marking needs each file
    ///     whole, as one string, so a page of a hundred files at <c>Index:MaxFileBytes</c> each was
    ///     gigabytes pulled into the server for a reply capped at kilobytes (GHSA-v284-9964-6mjr). Eight
    ///     MiB is a hundred ordinary source files many times over. The page's first file is read
    ///     whatever its size, so a file larger than this still shows its matches on a page of its own.
    /// </summary>
    public const int MaxMultilinePageMiB = 8;

    /// <summary>
    ///     The most lines one file's multiline matches may mark and show. <see cref="MaxLinesPerFile" />
    ///     counts matches, and one match can span the whole file — <c>(?s).*</c> does — so without this
    ///     it marked and returned every line (GHSA-v284-9964-6mjr). It is the most single-line mode can
    ///     ever show for a file: every match a line of its own, with the widest context either side.
    /// </summary>
    public const int MaxMultilineLinesShown = MaxLinesPerFile * ((2 * MaxContext) + 1);

    /// <summary><see cref="MaxMultilinePageMiB" /> in bytes, which is what file sizes are compared in.</summary>
    public const long MaxMultilinePageBytes = MaxMultilinePageMiB * 1024L * 1024;

    /// <summary>
    ///     The most candidate file content one multiline search reads to count its matches (#297).
    ///     Counting reads every candidate whole inside DuckDB, so before this a pattern with no literal
    ///     to narrow by read the whole index: 367 MiB and ten seconds on Radix, for any caller who asked.
    ///     Sixty-four MiB is about a second of that same pass, and it holds the largest repository there
    ///     (acslib, 61 MiB) whole, so a search scoped to one repository still gets an exact count. The
    ///     first candidate is counted whatever its size, as the page's first file is read.
    /// </summary>
    public const int MaxMultilineCountMiB = 64;

    /// <summary><see cref="MaxMultilineCountMiB" /> in bytes, which is what file sizes are compared in.</summary>
    public const long MaxMultilineCountBytes = MaxMultilineCountMiB * 1024L * 1024;

    /// <summary>
    ///     Wrapped around every multiline match by <c>regexp_replace</c> so the exact boundaries come
    ///     back from RE2 itself. Control characters, because source text does not contain them; a
    ///     stray one would only shift a line number by one within that file.
    /// </summary>
    private const char _matchStart = '';

    private const char _matchEnd = '';

    /// <summary>
    ///     Every search goes through here — the MCP tool, the operator endpoint and whatever comes
    ///     next — which is what makes this the one place a search is recorded. A new entry point cannot
    ///     report a different set of attributes, because it does not record at all.
    /// </summary>
    public Task<Outcome> SearchAsync(string slug, GrepRequest request, CancellationToken cancellationToken) =>
        // The engine is read off the answer here and nowhere else: this is the one search that picks
        // between full-text and a substring scan at query time.
        Telemetry.Search(slug, (GrepResult result) => result.Engine,
            () => RunAsync(slug, request, cancellationToken),
            (GrepResult result) => new Telemetry.Measured(result.TotalFiles, result.TotalLines));

    private async Task<Outcome> RunAsync(string slug, GrepRequest request, CancellationToken cancellationToken)
    {
        bool regex = request.Regex || request.Multiline;
        if (PatternQuery.Refusal(request.Query, request.Filter, regex,
                "The query is empty. Pass the text or RE2 pattern to search for.", out string query) is { } refused)
            return refused;

        return await readers.OverIndexAsync(slug, null,
            (index, token) => QueryAsync(index, request, query, regex, token), cancellationToken);
    }

    private static Task<Outcome> QueryAsync(IndexReader index, GrepRequest request, string query, bool regex,
        CancellationToken cancellationToken)
    {
        var bounds = Bounds.From(request);
        // A text query hands a parser nothing, so nothing it raises is about the query.
        if (!regex) return SearchLinesAsync(index, request, query, regex, bounds, cancellationToken);
        // Only a wrapped pattern needs compiling alone first: whole words and multiline wrap it.
        return PatternQuery.GuardedAsync(index.Connection, query, request.WholeWord || request.Multiline,
            request.Multiline ? MultilineFlags(request.CaseSensitive) : PatternQuery.LineFlags(request.CaseSensitive),
            () => request.Multiline
                ? SearchMultilineAsync(index.Connection, request, query, bounds, cancellationToken)
                : SearchLinesAsync(index, request, query, regex, bounds, cancellationToken),
            cancellationToken);
    }

    private static async Task<Outcome> SearchLinesAsync(
        IndexReader index, GrepRequest request, string query, bool regex, Bounds bounds,
        CancellationToken cancellationToken)
    {
        var connection = index.Connection;
        // Match parameters and file-filter parameters are kept apart so the "without filters" recount
        // below carries only what it references. Nothing would reject the extras — DuckDB.NET 1.5.5
        // skips a named parameter the statement does not use — but a recount that shows its whole
        // input is easier to read.
        var matchParameters = new List<DuckDBParameter>();
        string match;
        string engine;

        if (regex)
        {
            engine = RegexEngine;
            match = PatternQuery.LineMatch;
            matchParameters.AddRange(PatternQuery.LineParameters(query, request.WholeWord, request.CaseSensitive));
        }
        else
        {
            string[] tokens = query.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool useTokens = tokens.Any(HasIndexableChars) && await index.HasFullTextAsync(cancellationToken);
            engine = useTokens ? TokenEngine : SubstringEngine;
            match = TextMatch(tokens, request.CaseSensitive, useTokens, matchParameters);
        }

        var fileParameters = new List<DuckDBParameter>();
        string fileFilter = request.Filter.Sql(fileParameters);

        // At context 0 the window around a match is the match, and the scan that decided a line
        // matched had its text in hand: joining `lines` again to fetch that text is a second scan of
        // the largest table for something already read. So the content rides along in `hits` instead —
        // 123 ms against 157 ms on Radix, same rows. With context there are neighbouring lines that
        // were never read and the join earns its place, and a files-only search wants no text at all.
        bool carried = !request.FilesOnly && bounds.Context == 0;

        // The carried columns, qualified for wherever they are being selected. One spelling, because
        // the three places that carry them have to carry the same ones or the CTEs stop lining up.
        string Carried(string alias) =>
            carried ? $", {alias}content{(request.WithHistory ? $", {alias}commit_id" : "")}" : "";

        string common = $"""
                         WITH hits AS (
                             SELECT l.file_id, l.line_number, f.qualified_path{Carried("l.")}
                             FROM lines l JOIN files f USING (file_id)
                             WHERE {match}{fileFilter}),
                         per_file AS (
                             SELECT file_id, qualified_path, count(*) AS n FROM hits GROUP BY ALL),
                         totals AS (
                             SELECT count(*) AS total_files, coalesce(sum(n), 0) AS total_lines FROM per_file),
                         page_files AS (
                             SELECT file_id, qualified_path, n FROM per_file
                             ORDER BY n DESC, qualified_path
                             LIMIT $page_size OFFSET $skip)
                         """;

        // The lines the answer shows: the kept ones and their context window, grouped so that
        // overlapping windows collapse into one run of lines — or, where the text was carried, the kept
        // lines themselves, because at context 0 the window is the matching line and every line in it
        // matched. The whole difference between the two shapes is here, so everything downstream of
        // `shown` is one query rather than two.
        string Shown() => carried
            ? $"""
               shown AS (
                   SELECT file_id, line_number{Carried("")}, true AS is_match
                   FROM kept)
               """
            : $"""
               shown AS (
                   SELECT l.file_id, l.line_number, l.content{(request.WithHistory ? ", l.commit_id" : "")},
                          bool_or(k.line_number = l.line_number) AS is_match
                   FROM kept k
                   JOIN lines l ON l.file_id = k.file_id
                               AND l.line_number BETWEEN k.line_number - $context AND k.line_number + $context
                   GROUP BY ALL)
               """;

        // totals drives the join so that a page past the end still returns one row carrying the totals;
        // otherwise an out-of-range page would read as "no matches". Files-only never touches line
        // content: the cheap way to size a broad query.
        string sql = request.FilesOnly
            ? $"""
               {common}
               SELECT t.total_files, t.total_lines, p.qualified_path, p.n AS match_count,
                      NULL::INTEGER AS line_number, NULL::VARCHAR AS content, NULL::BOOLEAN AS is_match
               FROM totals t LEFT JOIN page_files p ON true
               ORDER BY p.n DESC, p.qualified_path
               """
            // kept = the matching lines shown per file, carrying their own text where `hits` had it.
            : $"""
               {common},
               kept AS (
                   SELECT file_id, line_number{Carried("")} FROM (
                       SELECT h.file_id, h.line_number{Carried("h.")},
                              row_number() OVER (PARTITION BY h.file_id ORDER BY h.line_number) AS rn
                       FROM hits h JOIN page_files p USING (file_id))
                   WHERE rn <= $max_lines),
               {Shown()},
               page_lines AS (
                   SELECT p.qualified_path, p.n, s.line_number, s.content, s.is_match{History(request)}
                   FROM page_files p JOIN shown s USING (file_id){HistoryJoin(request)})
               SELECT t.total_files, t.total_lines, p.qualified_path, p.n AS match_count,
                      p.line_number, p.content, p.is_match{Columns(request)}
               FROM totals t LEFT JOIN page_lines p ON true
               ORDER BY p.n DESC, p.qualified_path, p.line_number
               """;

        var files = new List<GrepFile>();
        int totalFiles = 0;
        long totalLines = 0;
        // The bounds are bound whether or not this shape reads them: DuckDB.NET skips a named parameter
        // the statement does not use, and one list is easier to read than one per shape.
        await using (var command = connection.Query(sql,
                         [
                             .. matchParameters, .. fileParameters, new("page_size", bounds.PageSize),
                             new("skip", bounds.Skip), new("context", bounds.Context),
                             new("max_lines", bounds.MaxLinesPerFile)
                         ]))
        await using (var reader = await command.ReaderAsync(cancellationToken))
        {
            string? currentPath = null;
            int currentCount = 0;
            List<GrepLine> current = [];
            while (await reader.ReadAsync(cancellationToken))
            {
                totalFiles = (int)reader.Int64("total_files");
                totalLines = reader.Int64("total_lines");
                if (reader.IsNull("qualified_path")) continue;

                string path = reader.Text("qualified_path");
                if (path != currentPath)
                {
                    if (currentPath is not null) files.Add(File(currentPath, currentCount, current));
                    currentPath = path;
                    currentCount = (int)reader.Int64("match_count");
                    current = [];
                }

                if (!reader.IsNull("line_number"))
                    current.Add(new GrepLine(reader.Int32("line_number"), reader.Text("content"),
                        reader.Flag("is_match"),
                        // Only asked for when the caller wanted history, and null for a line the build
                        // could not attribute — which is every line of a project indexed before there
                        // was any history to attribute from. The attribution's four columns come from
                        // one LEFT JOIN, so the helper's null test on the SHA answers for all four.
                        request.WithHistory ? reader.Attribution() : null));
            }

            if (currentPath is not null) files.Add(File(currentPath, currentCount, current));
        }

        int? withoutFilters = null;
        if (totalFiles == 0 && request.HasFileFilters)
            // Same match, no file filters: a second pass only on the empty answer, so the common case pays nothing.
            withoutFilters = await PatternQuery.FilesMatchingAsync(connection, match, matchParameters, cancellationToken);

        return new GrepResult(engine, totalFiles, totalLines, bounds.Page, bounds.PageSize, files, withoutFilters);

        // In single-line mode every marked line is exactly one match.
        static GrepFile File(string path, int count, List<GrepLine> lines)
        {
            return new GrepFile(path, count, lines.Count(l => l.IsMatch), lines);
        }
    }

    /// <summary>
    ///     The attribution columns, added to a grep only when the caller asked for history. Left out
    ///     otherwise rather than selected and ignored: this is the widest query in the codebase and it
    ///     runs on every search, so the default pays nothing for a feature it is not using.
    /// </summary>
    private static string History(GrepRequest request) =>
        request.WithHistory ? ", c.sha, c.author_name, c.authored_at, c.subject" : "";

    /// <summary>
    ///     A LEFT JOIN and not an inner one: a line the build could not attribute still has to come back
    ///     as a line. An inner join would silently drop it from the search results, which would make
    ///     asking for history change which code a grep finds.
    /// </summary>
    private static string HistoryJoin(GrepRequest request) =>
        request.WithHistory ? " LEFT JOIN commits c ON c.commit_id = s.commit_id" : "";

    private static string Columns(GrepRequest request) =>
        request.WithHistory ? ", p.sha, p.author_name, p.authored_at, p.subject" : "";

    /// <summary>
    ///     Whole-file regex, reassembled in the engine because the index stores text only as lines (#4).
    ///     Candidates are narrowed by the file filters and by a literal the pattern requires, then each
    ///     candidate is <c>string_agg</c>-ed in line order and matched with the <c>s</c> flag so <c>.</c>
    ///     crosses newlines. Two passes: counts without content, then content only for the page shown.
    /// </summary>
    /// <summary>
    ///     The RE2 flags of a multiline search: <c>s</c> so <c>.</c> crosses newlines, and case-insensitive
    ///     unless the caller asked otherwise. One spelling for the search and for the compile alone
    ///     that guards it (<see cref="PatternQuery.GuardedAsync" />).
    /// </summary>
    private static string MultilineFlags(bool caseSensitive) => caseSensitive ? "s" : "si";

    private static async Task<Outcome> SearchMultilineAsync(
        DuckDBConnection connection, GrepRequest request, string query, Bounds bounds,
        CancellationToken cancellationToken)
    {
        string flags = MultilineFlags(request.CaseSensitive);
        // Whole words are counted and marked from the form whose group 2 is the match
        // (SymbolText.WholeWordMatches): the whole-word test alone loses a match one character from the
        // last. That form runs every match over the text skipped before it, so it is extracted only from
        // a document the whole-word test has already found a match in; the plain pattern's extract is
        // empty exactly where there is none, and needs no test in front of it.
        bool wholeWord = request.WholeWord;
        var marking = wholeWord
            ? new DuckDBParameter("words", SymbolText.WholeWordMatches(query))
            : new DuckDBParameter("q", query);
        List<DuckDBParameter> matchParameters = wholeWord
            ? [new("q", SymbolText.WholeWord(query)), marking, new("flags", flags)]
            : [marking, new("flags", flags)];
        string matchCount = wholeWord
            ? """
              CASE WHEN regexp_matches(content, $q, $flags)
                   THEN len(list_filter(regexp_extract_all(content, $words, 2, $flags), v -> v <> '')) ELSE 0 END
              """
            : "len(regexp_extract_all(content, $q, 0, $flags))";
        string literalFilter = LiteralPrefilter(query, request.CaseSensitive, matchParameters);

        var fileParameters = new List<DuckDBParameter>();
        string fileFilter = request.Filter.Sql(fileParameters);

        // The extract alone decides both whether a document matches and how often: it is empty exactly
        // when regexp_matches is false, empty matches and empty documents included, so testing
        // regexp_matches first would run the pattern over every matching document twice.
        // `uncounted` drives the join so that a search with no match still returns the one row saying
        // how many candidates the budget left out.
        var counts = new List<(long FileId, string Path, int Count, long Bytes)>();
        int notSearched = 0;
        await using (var command = connection.Query($"""
                                                     {CountedDocuments(fileFilter + literalFilter)}
                                                     SELECT u.n AS not_searched, m.file_id, m.qualified_path, m.size_bytes,
                                                            m.match_count
                                                     FROM uncounted u LEFT JOIN (
                                                         SELECT file_id, qualified_path, size_bytes,
                                                                {matchCount} AS match_count
                                                         FROM docs) m ON m.match_count > 0
                                                     ORDER BY m.match_count DESC, m.qualified_path
                                                     """, [.. matchParameters, .. fileParameters]))
        await using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                notSearched = (int)reader.Int64("not_searched");
                if (reader.IsNull("file_id")) continue;
                counts.Add((reader.Int64("file_id"), reader.Text("qualified_path"),
                    (int)reader.Int64("match_count"), reader.Int64("size_bytes")));
            }
        }

        // The recount drops the filters, so it reads a superset of what the count read: after a count the
        // budget cut, it would be cut too and could not say that nothing matches outside. Where it is cut
        // and finds nothing, whether anything matches outside is not known, which is not the same as none.
        // Where it is cut and finds some, their number is a lower bound, reported as found: what the
        // caller acts on is that the filters hid matches, and that much is certain.
        int? withoutFilters = null;
        int notSearchedOutside = 0;
        if (counts.Count == 0 && notSearched == 0 && request.HasFileFilters)
        {
            await using var command = connection.Query($"""
                                                        {CountedDocuments(literalFilter)}
                                                        SELECT (SELECT n FROM uncounted) AS not_searched,
                                                               count(*) AS matching
                                                        FROM docs WHERE regexp_matches(content, $q, $flags)
                                                        """, matchParameters);
            await using var reader = await command.ReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            int matching = (int)reader.Int64("matching");
            int outside = (int)reader.Int64("not_searched");
            if (matching == 0 && outside > 0) notSearchedOutside = outside;
            else withoutFilters = matching;
        }

        long totalMatches = counts.Sum(c => (long)c.Count);
        var pageFiles = counts.Skip((int)Math.Min(bounds.Skip, counts.Count)).Take(bounds.PageSize).ToList();
        if (request.FilesOnly || pageFiles.Count == 0)
            return new GrepResult(MultilineEngine, counts.Count, totalMatches, bounds.Page, bounds.PageSize,
                [.. pageFiles.Select(f => new GrepFile(f.Path, f.Count, 0, []))], withoutFilters, notSearched,
                notSearchedOutside);

        // RE2 marks its own match boundaries: the pattern becomes group 1 and every match is rewritten as
        // START match END, so the offsets read back are exact even for \b, ^ or $, which a text search
        // for the matched string could not honour. The ids come from the query above, never from the
        // request, so inlining them is safe.
        // A whole-word match carries the text skipped before it and the boundary after it, and the rest
        // of the document after the last one is a match too, so each is written back whole after its
        // marked groups 1 and 2: the only groups a rewrite can name without counting the caller's are 0,
        // 1 and 2, and group 0 is the only one holding the boundary. WithoutMarkedCopies then puts the
        // skipped text back in front of the marked match and drops the copies group 0 repeats.
        // Marking reads each file whole, so the page's files are read while they fit the byte budget
        // (MaxMultilinePageBytes). The first always is, or a file larger than the budget could never be
        // shown; a later one that does not fit is skipped rather than ending the walk, so a small file
        // after a large one is still read.
        var read = new HashSet<long>();
        long bytes = 0;
        foreach (var file in pageFiles)
        {
            if (read.Count > 0 && bytes + file.Bytes > MaxMultilinePageBytes) continue;
            read.Add(file.FileId);
            bytes += file.Bytes;
        }

        string ids = string.Join(",", read.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        string rewrite = wholeWord
            ? @"$words, chr(1) || '\1' || chr(2) || chr(1) || '\2' || chr(2) || '\0'"
            : @"'(' || $q || ')', chr(1) || '\1' || chr(2)";
        var marked = new Dictionary<long, string>();
        await using (var command = connection.Query($"""
                                                     {Documents($" AND f.file_id IN ({ids})")}
                                                     SELECT file_id,
                                                            regexp_replace(content, {rewrite}, $gflags) AS marked
                                                     FROM docs
                                                     """,
                         [marking, new DuckDBParameter("gflags", flags + "g")]))
        await using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string text = reader.Text("marked");
                marked[reader.Int64("file_id")] = wholeWord ? WithoutMarkedCopies(text) : text;
            }
        }

        var files = new List<GrepFile>();
        foreach ((long fileId, string path, int count, _) in pageFiles)
        {
            if (!read.Contains(fileId))
            {
                files.Add(new GrepFile(path, count, 0, [], Unread: true));
                continue;
            }

            if (!marked.TryGetValue(fileId, out string? content)) continue;
            (var lines, int shown) = SpannedLines(content, bounds);
            files.Add(new GrepFile(path, count, shown, lines));
        }

        return new GrepResult(MultilineEngine, counts.Count, totalMatches, bounds.Page, bounds.PageSize, files,
            withoutFilters, notSearched, notSearchedOutside);

        // Skipped files (binary, oversized) have no lines and would aggregate to nothing; filtered out
        // here so they never even reach the join.
        static string Documents(string candidateFilter)
        {
            return $"""
                    WITH candidates AS (
                        SELECT f.file_id, f.qualified_path, f.size_bytes FROM files f
                        WHERE f.skip_reason IS NULL{candidateFilter}),
                    {Aggregated("candidates")}
                    """;
        }

        // The candidates in path order, read while their running size stays within MaxMultilineCountBytes
        // and the first whatever its size; the running size only grows, so what is read is a prefix and
        // "the files after X" is all a reply has to say about the rest. `uncounted` is how many were
        // left out. The budget is a constant, so inlining it is safe. Both read the candidates, which are
        // materialized so the literal prefilter runs over `lines` once, not once for each.
        static string CountedDocuments(string candidateFilter)
        {
            return $"""
                    WITH candidates AS MATERIALIZED (
                        SELECT f.file_id, f.qualified_path, f.size_bytes,
                               row_number() OVER running = 1
                                   OR sum(f.size_bytes) OVER running <= {MaxMultilineCountBytes} AS in_budget
                        FROM files f
                        WHERE f.skip_reason IS NULL{candidateFilter}
                        WINDOW running AS (ORDER BY f.qualified_path ROWS UNBOUNDED PRECEDING)),
                    counted AS (
                        SELECT file_id, qualified_path, size_bytes FROM candidates WHERE in_budget),
                    uncounted AS (
                        SELECT count(*) FILTER (WHERE NOT in_budget) AS n FROM candidates),
                    {Aggregated("counted")}
                    """;
        }

        static string Aggregated(string files)
        {
            return $"""
                    docs AS (
                        SELECT c.file_id, c.qualified_path, c.size_bytes,
                               string_agg(l.content, chr(10) ORDER BY l.line_number) AS content
                        FROM {files} c JOIN lines l USING (file_id)
                        GROUP BY ALL)
                    """;
        }
    }

    /// <summary>Request numbers clamped to the ranges the tool description promises.</summary>
    private readonly record struct Bounds(int Page, int PageSize, int Context, int MaxLinesPerFile)
    {
        public static Bounds From(GrepRequest request) => new(
            Math.Max(1, request.Page),
            Math.Clamp(request.PageSize, 1, MaxPageSize),
            Math.Clamp(request.Context, 0, MaxContext),
            Math.Clamp(request.MaxLinesPerFile, 1, GrepSearch.MaxLinesPerFile));

        /// <summary>The files ahead of this page.</summary>
        public long Skip => Paging.Skip(Page, PageSize);
    }
}
