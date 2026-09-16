using System.Globalization;
using System.Text;
using DuckDB.NET.Data;

namespace CodeExplorer;

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

/// <summary><see cref="IsMatch" /> is false for a line returned only as context around a match.</summary>
/// <summary>
///     One line of an answer. <see cref="By" /> is filled only when the caller asked for history and the
///     build attributed the line; it says who last changed it, never who wrote it (CONTEXT.md,
///     Attribution).
/// </summary>
public sealed record GrepLine(int LineNumber, string Text, bool IsMatch, AttributedBy? By = null);

/// <summary>
///     One file's share of the answer. <see cref="MatchesShown" /> is how many of the
///     <see cref="MatchCount" /> matches <see cref="Lines" /> covers; in multiline mode one match can
///     span several lines, so it is not the number of marked lines. Both are zero-lines for a files-only search.
/// </summary>
public sealed record GrepFile(string QualifiedPath, int MatchCount, int MatchesShown, IReadOnlyList<GrepLine> Lines);

/// <summary>
///     A page of matches; the answer when the search was not a <see cref="SearchProblem" />.
///     <see cref="Engine" /> names what answered, because full-text and substring
///     scan rank differently. <see cref="FilesMatchingWithoutFilters" /> is filled only when nothing
///     matched under path, extension or exclude filters: it tells "no matches" from "matches existed
///     and the filters hid them", which read identically and mean opposite things.
/// </summary>
public sealed record GrepResult(
    string Engine,
    int TotalFiles,
    long TotalLines,
    int Page,
    int PageSize,
    IReadOnlyList<GrepFile> Files,
    int? FilesMatchingWithoutFilters) : SearchOutcome;

/// <summary>
///     Text and regular-expression search over a project's <c>lines</c>. All matching runs inside
///     DuckDB (ADR-0004): BM25 narrows a text query when the index has a full-text index, every
///     token is then verified with <c>contains</c> so the answer is exact; regex is RE2 through
///     <c>regexp_matches</c>. The substring fallback for text queries is <c>contains</c> rather than
///     the <c>regexp_matches</c> ADR-0004 names, because a text query is not a pattern and escaping it
///     into one buys nothing. Nothing here opens <c>control.duckdb</c> (ADR-0005).
/// </summary>
public sealed class GrepSearch(ProjectIndexes indexes)
{
    public const string FullTextEngine = "full-text";
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
    ///     Wrapped around every multiline match by <c>regexp_replace</c> so the exact boundaries come
    ///     back from RE2 itself. Control characters, because source text does not contain them; a
    ///     stray one would only shift a line number by one within that file.
    /// </summary>
    private const char MatchStart = '';

    private const char MatchEnd = '';

    /// <summary>
    ///     Every search goes through here — the MCP tool, the operator endpoint and whatever comes
    ///     next — which is what makes this the one place a search is recorded. A new entry point cannot
    ///     report a different set of attributes, because it does not record at all.
    /// </summary>
    public async Task<SearchOutcome> SearchAsync(string slug, GrepRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await RunAsync(slug, request, cancellationToken);
        if (outcome is GrepResult result) recording.Matched(result.Engine, result.TotalFiles, result.TotalLines);
        else recording.Problem();
        return outcome;
    }

    private async Task<SearchOutcome> RunAsync(string slug, GrepRequest request, CancellationToken cancellationToken)
    {
        string query = request.Query.Trim();
        if (query.Length == 0)
            return new SearchProblem("The query is empty. Pass the text or RE2 pattern to search for.");

        bool regex = request.Regex || request.Multiline;
        if (regex && Re2.Unsupported(query) is { } unsupported) return new SearchProblem(unsupported);
        if (regex && request.WholeWord) query = $@"\b(?:{query})\b";

        var open = await IndexReader.OpenAsync(indexes, slug, null, cancellationToken);
        if (open is IndexOpen.Refused refused) return new SearchProblem(refused.Explanation);
        using var index = ((IndexOpen.Opened)open).Reader;
        var connection = index.Connection;

        var bounds = Bounds.From(request);
        try
        {
            return request.Multiline
                ? await SearchMultilineAsync(connection, request, query, bounds, cancellationToken)
                : await SearchLinesAsync(index, request, query, regex, bounds, cancellationToken);
        }
        catch (DuckDBException ex) when (regex && Re2.IsPatternRejection(ex))
        {
            // Only regex mode hands user text to a parser; anything else DuckDB raises here is
            // infrastructure and propagates.
            return new SearchProblem(Re2.Rejected(ex));
        }
    }

    private static async Task<SearchOutcome> SearchLinesAsync(
        IndexReader index, GrepRequest request, string query, bool regex, Bounds bounds,
        CancellationToken cancellationToken)
    {
        var connection = index.Connection;
        // Match parameters and file-filter parameters are kept apart: the "without filters" recount
        // below reuses the match alone, and DuckDB rejects a parameter the statement does not reference.
        var matchParameters = new List<DuckDBParameter>();
        var match = new StringBuilder();
        string engine;

        if (regex)
        {
            engine = RegexEngine;
            match.Append("regexp_matches(l.content, $q, $flags)");
            matchParameters.Add(new DuckDBParameter("q", query));
            matchParameters.Add(new DuckDBParameter("flags", request.CaseSensitive ? "" : "i"));
        }
        else
        {
            string[] tokens = query.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool useFts = tokens.Any(HasIndexableChars) && await index.HasFullTextAsync(cancellationToken);
            engine = useFts ? FullTextEngine : SubstringEngine;

            if (useFts)
            {
                // Conjunctive: every token must be in the line. The index is lower-cased, so this is a
                // case-insensitive prefilter; the contains() below restores exactness either way.
                match.Append("fts_main_lines.match_bm25(l.line_id, $q, conjunctive := 1) IS NOT NULL");
                matchParameters.Add(new DuckDBParameter("q", query));
            }

            for (int i = 0; i < tokens.Length; i++)
            {
                if (match.Length > 0) match.Append(" AND ");
                match.Append(request.CaseSensitive
                    ? $"contains(l.content, $t{i})"
                    : $"contains(lower(l.content), $t{i})");
                matchParameters.Add(new DuckDBParameter($"t{i}",
                    request.CaseSensitive ? tokens[i] : tokens[i].ToLowerInvariant()));
            }
        }

        var fileParameters = new List<DuckDBParameter>();
        string fileFilter = request.Filter.Sql(fileParameters);
        string common = $"""
                         WITH hits AS (
                             SELECT l.file_id, l.line_number, f.qualified_path
                             FROM lines l JOIN files f USING (file_id)
                             WHERE {match}{fileFilter}),
                         per_file AS (
                             SELECT file_id, qualified_path, count(*) AS n FROM hits GROUP BY ALL),
                         totals AS (
                             SELECT count(*) AS total_files, coalesce(sum(n), 0) AS total_lines FROM per_file),
                         page_files AS (
                             SELECT file_id, qualified_path, n FROM per_file
                             ORDER BY n DESC, qualified_path
                             LIMIT {bounds.PageSize} OFFSET {(bounds.Page - 1) * bounds.PageSize})
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
            // kept = the matching lines shown per file; shown = those plus their context window, grouped
            // so overlapping windows collapse into one run of lines.
            : $"""
               {common},
               kept AS (
                   SELECT file_id, line_number FROM (
                       SELECT h.file_id, h.line_number,
                              row_number() OVER (PARTITION BY h.file_id ORDER BY h.line_number) AS rn
                       FROM hits h JOIN page_files p USING (file_id))
                   WHERE rn <= {bounds.MaxLinesPerFile}),
               shown AS (
                   SELECT l.file_id, l.line_number, l.content{(request.WithHistory ? ", l.commit_id" : "")},
                          bool_or(k.line_number = l.line_number) AS is_match
                   FROM kept k
                   JOIN lines l ON l.file_id = k.file_id
                               AND l.line_number BETWEEN k.line_number - {bounds.Context} AND k.line_number + {bounds.Context}
                   GROUP BY ALL),
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
        using (var command = connection.Query(sql, [.. matchParameters, .. fileParameters]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
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
                        // was any history to attribute from.
                        request.WithHistory && !reader.IsNull("author_name")
                            ? new AttributedBy(reader.Text("sha"), reader.Text("author_name"),
                                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")),
                                reader.Text("subject"))
                            : null));
            }

            if (currentPath is not null) files.Add(File(currentPath, currentCount, current));
        }

        int? withoutFilters = null;
        if (totalFiles == 0 && request.HasFileFilters)
            // Same match, no file filters: a second pass only on the empty answer, so the common case pays nothing.
            withoutFilters = (int)await connection.CountAsync(
                $"SELECT count(DISTINCT l.file_id) FROM lines l WHERE {match}", matchParameters, cancellationToken);

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
    private static async Task<SearchOutcome> SearchMultilineAsync(
        DuckDBConnection connection, GrepRequest request, string query, Bounds bounds,
        CancellationToken cancellationToken)
    {
        string flags = request.CaseSensitive ? "s" : "si";
        var matchParameters = new List<DuckDBParameter> { new("q", query), new("flags", flags) };
        string literalFilter = "";
        if (RequiredLiteral(query) is { } literal)
        {
            // The literal holds no newline, so a whole-file match must contain it within one line. Compared
            // lower-cased regardless of case mode: a (?i) inside the pattern would otherwise defeat it.
            literalFilter =
                " AND EXISTS (SELECT 1 FROM lines l WHERE l.file_id = f.file_id AND contains(lower(l.content), $lit))";
            matchParameters.Add(new DuckDBParameter("lit", literal.ToLowerInvariant()));
        }

        var fileParameters = new List<DuckDBParameter>();
        string fileFilter = request.Filter.Sql(fileParameters);

        var counts = new List<(long FileId, string Path, int Count)>();
        using (var command = connection.Query($"""
                                                  {Documents(fileFilter + literalFilter)}
                                                  SELECT file_id, qualified_path, len(regexp_extract_all(content, $q, 0, $flags)) AS match_count
                                                  FROM docs WHERE regexp_matches(content, $q, $flags)
                                                  ORDER BY match_count DESC, qualified_path
                                                  """, [.. matchParameters, .. fileParameters]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                counts.Add((reader.Int64("file_id"), reader.Text("qualified_path"),
                    (int)reader.Int64("match_count")));
        }

        int? withoutFilters = null;
        if (counts.Count == 0 && request.HasFileFilters)
            withoutFilters = (int)await connection.CountAsync(
                $"{Documents(literalFilter)} SELECT count(*) FROM docs WHERE regexp_matches(content, $q, $flags)",
                matchParameters, cancellationToken);

        long totalMatches = counts.Sum(c => (long)c.Count);
        var pageFiles = counts.Skip((bounds.Page - 1) * bounds.PageSize).Take(bounds.PageSize).ToList();
        if (request.FilesOnly || pageFiles.Count == 0)
            return new GrepResult(MultilineEngine, counts.Count, totalMatches, bounds.Page, bounds.PageSize,
                [.. pageFiles.Select(f => new GrepFile(f.Path, f.Count, 0, []))], withoutFilters);

        // RE2 marks its own match boundaries: the pattern becomes group 1 and every match is rewritten as
        // START match END, so the offsets read back are exact even for \b, ^ or $, which a text search
        // for the matched string could not honour. The ids come from the query above, never from the
        // request, so inlining them is safe.
        string ids = string.Join(",", pageFiles.Select(f => f.FileId.ToString(CultureInfo.InvariantCulture)));
        var marked = new Dictionary<long, string>();
        using (var command = connection.Query($"""
                                                  {Documents($" AND f.file_id IN ({ids})")}
                                                  SELECT file_id,
                                                         regexp_replace(content, '(' || $q || ')', chr(1) || '\1' || chr(2), $gflags) AS marked
                                                  FROM docs
                                                  """,
                   [new DuckDBParameter("q", query), new DuckDBParameter("gflags", flags + "g")]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) marked[reader.Int64("file_id")] = reader.Text("marked");
        }

        var files = new List<GrepFile>();
        foreach ((long fileId, string path, int count) in pageFiles)
        {
            if (!marked.TryGetValue(fileId, out string? content)) continue;
            (var lines, int shown) = SpannedLines(content, bounds);
            files.Add(new GrepFile(path, count, shown, lines));
        }

        return new GrepResult(MultilineEngine, counts.Count, totalMatches, bounds.Page, bounds.PageSize, files,
            withoutFilters);

        // Skipped files (binary, oversized) have no lines and would aggregate to nothing; filtered out
        // here so they never even reach the join.
        static string Documents(string candidateFilter)
        {
            return $"""
                    WITH candidates AS (
                        SELECT f.file_id, f.qualified_path FROM files f
                        WHERE f.skip_reason IS NULL{candidateFilter}),
                    docs AS (
                        SELECT c.file_id, c.qualified_path,
                               string_agg(l.content, chr(10) ORDER BY l.line_number) AS content
                        FROM candidates c JOIN lines l USING (file_id)
                        GROUP BY ALL)
                    """;
        }
    }

    /// <summary>
    ///     Walks content in which every match is wrapped in <see cref="MatchStart" /> and
    ///     <see cref="MatchEnd" />, marks every line a match spans, adds the context window, and returns
    ///     the lines with how many matches they cover. Empty matches are skipped: they span nothing.
    /// </summary>
    private static (List<GrepLine> Lines, int MatchesShown) SpannedLines(string marked, Bounds bounds)
    {
        var lines = new List<string>();
        var matched = new HashSet<int>();
        var shown = new SortedSet<int>();
        var current = new StringBuilder();
        int line = 1, matchesSeen = 0, matchStartLine = 0, matchStartColumn = 0;
        foreach (char c in marked)
            switch (c)
            {
                case MatchStart:
                    matchStartLine = line;
                    matchStartColumn = current.Length;
                    break;
                case MatchEnd:
                    if (matchStartLine == line && current.Length == matchStartColumn) break;
                    if (matchesSeen < bounds.MaxLinesPerFile)
                    {
                        for (int i = matchStartLine; i <= line; i++) matched.Add(i);
                        for (int i = Math.Max(1, matchStartLine - bounds.Context); i <= line + bounds.Context; i++)
                            shown.Add(i);
                    }

                    matchesSeen++;
                    break;
                case '\n':
                    lines.Add(current.ToString());
                    current.Clear();
                    line++;
                    break;
                default:
                    current.Append(c);
                    break;
            }

        lines.Add(current.ToString());
        return (
            [.. shown.Where(i => i <= lines.Count).Select(i => new GrepLine(i, lines[i - 1], matched.Contains(i)))],
            Math.Min(matchesSeen, bounds.MaxLinesPerFile));
    }

    /// <summary>
    ///     A literal every match of the pattern must contain, or null. Ripgrep's "inner literal" idea,
    ///     reduced to what is provably sound: only characters at the top level (outside any group or
    ///     class), not under a quantifier that allows zero, and never when the top level has an
    ///     alternation. A shorter literal than ripgrep would find costs a weaker prefilter, never a
    ///     wrong answer. Exposed for tests.
    /// </summary>
    internal static string? RequiredLiteral(string pattern)
    {
        var best = new StringBuilder();
        var current = new StringBuilder();
        int depth = 0;

        void Break()
        {
            if (current.Length > best.Length) best.Clear().Append(current);
            current.Clear();
        }

        // Keeps c only when the next character cannot let it match zero times; '+' keeps it but ends
        // the run, because "ab+c" matches "abbc", which does not contain "abc".
        void Literal(char c, char next)
        {
            if (depth != 0) return;
            if (next is '?' or '*' or '{')
            {
                Break();
                return;
            }

            current.Append(c);
            if (next == '+') Break();
        }

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            char next = i + 1 < pattern.Length ? pattern[i + 1] : '\0';
            switch (c)
            {
                case '|' when depth == 0:
                    return null;
                case '\\':
                    if (++i >= pattern.Length) break;
                    // \d, \s, \b, \1 and the like are classes or anchors; \. and \( are the character itself.
                    if (char.IsLetterOrDigit(pattern[i])) Break();
                    else Literal(pattern[i], i + 1 < pattern.Length ? pattern[i + 1] : '\0');
                    break;
                case '(':
                    depth++;
                    Break();
                    break;
                case ')':
                    depth--;
                    Break();
                    break;
                case '[':
                    // Skip the class: a leading ']' or '^]' is literal, and '\' escapes inside it.
                    i++;
                    if (i < pattern.Length && pattern[i] == '^') i++;
                    if (i < pattern.Length && pattern[i] == ']') i++;
                    while (i < pattern.Length && pattern[i] != ']')
                        i += pattern[i] == '\\' ? 2 : 1;
                    Break();
                    break;
                case '{':
                    // A counted repetition; its digits are not literals.
                    while (i < pattern.Length && pattern[i] != '}') i++;
                    Break();
                    break;
                case '.' or '^' or '$' or '?' or '*' or '+' or '}':
                    Break();
                    break;
                default:
                    Literal(c, next);
                    break;
            }
        }

        Break();
        string literal = best.ToString();
        return literal.Trim().Length == 0 ? null : literal;
    }

    /// <summary>The FTS tokenizer keeps letters, digits and underscore; a token with none of them has no index entry.</summary>
    private static bool HasIndexableChars(string token) => token.Any(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Request numbers clamped to the ranges the tool description promises.</summary>
    private readonly record struct Bounds(int Page, int PageSize, int Context, int MaxLinesPerFile)
    {
        public static Bounds From(GrepRequest request) => new(
            Math.Max(1, request.Page),
            Math.Clamp(request.PageSize, 1, MaxPageSize),
            Math.Clamp(request.Context, 0, MaxContext),
            Math.Clamp(request.MaxLinesPerFile, 1, GrepSearch.MaxLinesPerFile));
    }
}
