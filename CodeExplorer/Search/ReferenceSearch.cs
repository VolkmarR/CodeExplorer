using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>Everything a find_references call asks for. Bounds are enforced by <see cref="ReferenceSearch" />.</summary>
/// <param name="Symbol">The identifier to look for, matched on word boundaries and case-sensitively.</param>
/// <param name="Filter">Which files to look in, the shape every index search takes (<see cref="FileFilter" />).</param>
/// <param name="MaxFiles">How many of the matching files to read, most matching lines first.</param>
public sealed record ReferenceRequest(
    string Symbol,
    FileFilter Filter,
    int MaxFiles = ReferenceSearch.DefaultMaxFiles);

/// <summary>
///     One place the identifier appears, with what it looks like and where it sits. A line naming the
///     identifier twice yields two of these, because <c>return Foo.Create(Foo.Default)</c> is a type
///     use and a read and reporting it as one of them loses the other.
///     <see cref="Scope" /> is the enclosing <c>Type.Member</c> where indentation was enough to
///     determine one, and null where it was not — an unknown scope is left unsaid rather than guessed.
/// </summary>
public sealed record Reference(string QualifiedPath, int LineNumber, string Text, ReferenceKind Kind, string? Scope);

/// <summary>
///     Everything read, already classified; the answer when the search was not a <see cref="SearchProblem" />. <see cref="TotalFiles" /> and <see cref="TotalLines" />
///     are project-wide under the filters, while <see cref="References" /> covers only the
///     <see cref="FilesExamined" /> files that were read, so a caller can say what it is not showing.
///     <see cref="FilesMatchingWithoutFilters" /> is filled whenever the request carried filters: a
///     thin answer whose declaration sits in an excluded file reads exactly like a symbol that is
///     never declared.
///     The counting lives here rather than in the tool that prints it. A second caller asking how
///     many writes there are must not be able to count them differently, and "which files were cut
///     short" is a question about this result, not about its formatting.
///     <see cref="TotalLines" /> counts the lines holding the identifier anywhere in the project
///     under the filters, which is not the number of references: one line may name it twice.
/// </summary>
public sealed record ReferenceResult(
    int TotalFiles,
    int FilesExamined,
    long TotalLines,
    IReadOnlyList<Reference> References,
    int? FilesMatchingWithoutFilters) : SearchOutcome
{
    /// <summary>The references that are code: everything but a comment, a string literal or an import.</summary>
    public int CodeReferences => References.Count - Noise;

    /// <summary>Mentions rather than uses, counted so that a quiet answer is never quiet by omission.</summary>
    public int Noise =>
        Count(ReferenceKind.Comment) + Count(ReferenceKind.StringLiteral) + Count(ReferenceKind.Import);

    /// <summary>How many of the references are of one kind.</summary>
    public int Count(ReferenceKind kind) => References.Count(r => r.Kind == kind);

    /// <summary>
    ///     The files that hold at least <see cref="ReferenceSearch.MaxLinesPerFile" /> matching lines
    ///     and were therefore read no further. Their unread lines could hold the declaration, so a
    ///     caller has to be able to say so.
    /// </summary>
    public IReadOnlyList<string> CutShortFiles =>
    [
        .. References.GroupBy(r => r.QualifiedPath)
            .Where(g => g.DistinctBy(r => r.LineNumber).Count() >= ReferenceSearch.MaxLinesPerFile)
            .Select(g => g.Key)
    ];
}

/// <summary>
///     Where an identifier is used, told apart from where it is merely mentioned (#11). The candidate
///     set comes from DuckDB — one RE2 <c>regexp_matches</c> per line, on word boundaries (ADR-0004) —
///     and <see cref="ReferenceClassifier" /> then says what each candidate line looks like. That
///     split is the whole design: the engine decides what matches and .NET decides what a match is,
///     so no answer depends on two matchers agreeing.
///     It is textual throughout and therefore evidence rather than proof, which every reply says in
///     so many words. Nothing here opens <c>control.duckdb</c> (ADR-0005).
/// </summary>
public sealed class ReferenceSearch(ProjectIndexes indexes)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell this apart from a grep.</summary>
    public const string Engine = "reference scan";

    /// <summary>
    ///     Enough files that a symbol used across a mid-sized service is covered in one call, few
    ///     enough that a name like <c>Id</c> does not read the whole project before saying it is too
    ///     common. The reply says how many files were left unread.
    /// </summary>
    public const int DefaultMaxFiles = 60;

    /// <summary>
    ///     A hundred files at the default twenty-odd references each is already past the reply cap, so
    ///     a larger ceiling would only be truncated; narrowing the filters is the honest way to see
    ///     more. The same number grep caps a page at, for the same reason.
    /// </summary>
    public const int MaxFiles = 100;

    /// <summary>
    ///     A file with more matching lines than this is telling the agent the name is too common to
    ///     enumerate. The same ceiling grep puts on lines per file, for the same reason.
    /// </summary>
    public const int MaxLinesPerFile = 200;

    /// <summary>RE2 metacharacters. Escaped one by one rather than with .NET's <c>Regex.Escape</c>,
    ///     which also escapes whitespace and <c>#</c> in ways RE2 does not accept.</summary>
    private const string Metacharacters = @"\.+*?()|[]{}^$";

    /// <summary>
    ///     Every reference search goes through here, which is what makes this the one place such a
    ///     search is recorded. It is the only public method for the same reason grep has one: a second
    ///     entry point has nothing else to call.
    /// </summary>
    public async Task<SearchOutcome> FindAsync(string slug, ReferenceRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await RunAsync(slug, request, cancellationToken);
        if (outcome is ReferenceResult result) recording.Matched(Engine, result.TotalFiles, result.TotalLines);
        else recording.Problem();
        return outcome;
    }

    private async Task<SearchOutcome> RunAsync(string slug, ReferenceRequest request,
        CancellationToken cancellationToken)
    {
        string symbol = request.Symbol.Trim();
        if (Unusable(symbol) is { } unusable) return new SearchProblem(unusable);

        var open = await IndexReader.OpenAsync(indexes, slug, request.Filter.Repository, cancellationToken);
        if (open is IndexOpen.Refused refused) return new SearchProblem(refused.Explanation);
        using var index = ((IndexOpen.Opened)open).Reader;
        var connection = index.Connection;

        // The slug the index holds, not the one the caller typed: the filter's subquery matches it exactly.
        var filter = request.Filter with { Repository = index.Repository?.Slug };
        int maxFiles = Math.Clamp(request.MaxFiles, 1, MaxFiles);
        string pattern = Pattern(symbol);

        var fileParameters = new List<DuckDBParameter>();
        string fileFilter = filter.Sql(fileParameters);

        // totals drives the join so that a symbol matching nothing still returns one row carrying the
        // zero counts, the way grep's page-past-the-end does.
        string sql = $"""
                      WITH hits AS (
                          SELECT l.file_id, l.line_number, l.content, f.qualified_path
                          FROM lines l JOIN files f USING (file_id)
                          WHERE regexp_matches(l.content, $q, ''){fileFilter}),
                      per_file AS (
                          SELECT file_id, qualified_path, count(*) AS n FROM hits GROUP BY ALL),
                      totals AS (
                          SELECT count(*) AS total_files, coalesce(sum(n), 0) AS total_lines FROM per_file),
                      page_files AS (
                          SELECT file_id, qualified_path FROM per_file
                          ORDER BY n DESC, qualified_path
                          LIMIT {maxFiles}),
                      kept AS (
                          SELECT file_id, qualified_path, line_number, content FROM (
                              SELECT h.file_id, p.qualified_path, h.line_number, h.content,
                                     row_number() OVER (PARTITION BY h.file_id ORDER BY h.line_number) AS rn
                              FROM hits h JOIN page_files p USING (file_id))
                          WHERE rn <= {MaxLinesPerFile})
                      SELECT t.total_files, t.total_lines, k.file_id, k.qualified_path, k.line_number, k.content
                      FROM totals t LEFT JOIN kept k ON true
                      ORDER BY k.qualified_path, k.line_number
                      """;

        var matchParameters = new List<DuckDBParameter> { new("q", pattern) };

        var matched = new List<MatchedLine>();
        int totalFiles = 0;
        long totalLines = 0;
        using (var command = connection.Query(sql, [.. matchParameters, .. fileParameters]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                totalFiles = (int)reader.Int64("total_files");
                totalLines = reader.Int64("total_lines");
                if (reader.IsNull("qualified_path")) continue;
                matched.Add(new MatchedLine(reader.Int64("file_id"), reader.Text("qualified_path"),
                    reader.Int32("line_number"), reader.Text("content")));
            }
        }

        // Unlike grep this recounts whenever filters were given rather than only on an empty answer:
        // the footgun here is a result that looks complete because the file declaring the symbol —
        // a generated partial, most often — was excluded.
        int? withoutFilters = null;
        if (filter.Any)
            withoutFilters = (int)await connection.CountAsync(
                "SELECT count(DISTINCT l.file_id) FROM lines l WHERE regexp_matches(l.content, $q, '')",
                matchParameters, cancellationToken);

        var scopes = await ScopesAsync(connection, matched, cancellationToken);
        // Every appearance on the line, not only the first: a line naming the identifier twice is two
        // references, and they are often of different kinds.
        var references = matched
            .SelectMany(line => ReferenceClassifier.Occurrences(line.Content, symbol)
                .Select(at => new Reference(line.Path, line.LineNumber, line.Content,
                    ReferenceClassifier.Classify(line.Content, symbol, at),
                    scopes.TryGetValue(line.FileId, out var declarations)
                        ? ReferenceClassifier.EnclosingScope(declarations, line.LineNumber,
                            ReferenceClassifier.Indent(line.Content))
                        : null)))
            .ToList();

        int filesExamined = matched.Select(line => line.FileId).Distinct().Count();
        return new ReferenceResult(totalFiles, filesExamined, totalLines, references, withoutFilters);
    }

    /// <summary>One line the engine matched, before it is taken apart into the references on it.</summary>
    private readonly record struct MatchedLine(long FileId, string Path, int LineNumber, string Content);

    /// <summary>
    ///     The declaration lines of every file a reference was read from, which is what an enclosing
    ///     scope is worked out from. DuckDB narrows each file to the few lines that could be a
    ///     declaration, so the whole file never leaves the index for the sake of a label on one line.
    /// </summary>
    private static async Task<Dictionary<long, List<DeclarationLine>>> ScopesAsync(
        DuckDBConnection connection, List<MatchedLine> matched, CancellationToken cancellationToken)
    {
        var scopes = new Dictionary<long, List<DeclarationLine>>();
        long[] fileIds = [.. matched.Select(line => line.FileId).Distinct()];
        if (fileIds.Length == 0) return scopes;

        // The ids came from the query above and never from the request, so inlining them is safe and
        // saves binding one parameter per file.
        string ids = string.Join(",", fileIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        using var command = connection.Query($"""
                                                 SELECT file_id, line_number, content FROM lines
                                                 WHERE file_id IN ({ids}) AND regexp_matches(content, $d, '')
                                                 ORDER BY file_id, line_number
                                                 """,
            [new DuckDBParameter("d", ReferenceClassifier.DeclarationCandidatePattern)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ReferenceClassifier.Declaration(reader.Int32("line_number"), reader.Text("content")) is not
                { } declaration) continue;
            if (!scopes.TryGetValue(reader.Int64("file_id"), out var declarations))
                scopes[reader.Int64("file_id")] = declarations = [];
            declarations.Add(declaration);
        }

        return scopes;
    }

    /// <summary>
    ///     Why this is not something to look for, or null. Malformed input must never come back as an
    ///     empty result: a caller acts on "nothing uses this", and a refusal shaped like one teaches
    ///     the agent something false.
    /// </summary>
    private static string? Unusable(string symbol)
    {
        if (symbol.Length == 0)
            return "No symbol given. Pass the identifier to look for, such as \"OrderStatus\".";
        if (symbol.Any(char.IsWhiteSpace))
            return $"\"{symbol}\" is not one identifier. find_references looks for a single name; "
                   + "use grep for a phrase, or regex=true for a pattern.";
        if (!symbol.Any(c => char.IsLetterOrDigit(c) || c == '_'))
            return $"\"{symbol}\" holds no identifier characters, so it names no symbol. "
                   + "Use grep for punctuation and operators.";
        return null;
    }

    /// <summary>
    ///     The identifier as an RE2 pattern: escaped, and anchored on a word boundary at each end that
    ///     has a word character to anchor to. A <c>\b</c> against punctuation would mean the opposite
    ///     of what it does against a letter, so it is left off there rather than applied blindly.
    /// </summary>
    private static string Pattern(string symbol)
    {
        string escaped = string.Concat(symbol.Select(c =>
            Metacharacters.Contains(c, StringComparison.Ordinal) ? $"\\{c}" : c.ToString()));
        string head = IsWord(symbol[0]) ? @"\b" : "";
        string tail = IsWord(symbol[^1]) ? @"\b" : "";
        return head + escaped + tail;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
