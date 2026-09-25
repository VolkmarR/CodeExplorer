using System.Globalization;
using CodeExplorer.Infrastructure;
using CodeExplorer.Language;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

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
///     <see cref="Evidence" /> is how the classification was reached (ADR-0008). Every reference
///     carries it, so a reply can say whether it is repeating a parser or reading line shape — claims
///     of different strength that must not read alike.
/// </summary>
public sealed record Reference(
    string QualifiedPath,
    int LineNumber,
    string Text,
    ReferenceKind Kind,
    string? Scope,
    Evidence Evidence);

/// <summary>
///     Everything read, already classified; the answer when the search was not a <see cref="Problem" />. <see cref="TotalFiles" /> and <see cref="TotalLines" />
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
///     <see cref="TotalOccurrences" /> is that same project-wide sweep counted per appearance rather
///     than per line, so it answers "how many, in the whole project" where <see cref="References" />
///     can only answer it for the sample that was read. It is a count and not a classification: an
///     occurrence here may turn out to be a call, a comment or an unrelated symbol of the same name.
/// </summary>
/// <remarks>
///     <see cref="Uncovered" /> is the extensions among the files holding the name that no language
///     profile covers (#126). Their lines are matched like any other — the pattern is the same in
///     every language — but what each appearance looks like was decided by the conservative default
///     shapes, so a clean set of counts over them is a weaker claim than the same counts over a
///     profiled language, and the reply has to be able to say which it is.
/// </remarks>
public sealed record ReferenceResult(
    int TotalFiles,
    int FilesExamined,
    long TotalLines,
    long TotalOccurrences,
    IReadOnlyList<Reference> References,
    int? FilesMatchingWithoutFilters,
    IReadOnlyList<UncoveredFiles> Uncovered) : Outcome
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
///     and the file's <see cref="ILanguageAnalyzer" /> then says what each candidate line looks like (ADR-0008). That
///     split is the whole design: the engine decides what matches and .NET decides what a match is,
///     so no answer depends on two matchers agreeing.
///     It is textual throughout and therefore evidence rather than proof, which every reply says in
///     so many words. Nothing here opens <c>control.duckdb</c> (ADR-0005).
/// </summary>
public sealed class ReferenceSearch(IndexReaders readers)
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

    /// <summary>
    ///     Every reference search goes through here, which is what makes this the one place such a
    ///     search is recorded. It is the only public method for the same reason grep has one: a second
    ///     entry point has nothing else to call.
    /// </summary>
    public Task<Outcome> FindAsync(string slug, ReferenceRequest request,
        CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => RunAsync(slug, request, cancellationToken),
            (ReferenceResult result) => new Telemetry.Measured(result.TotalFiles, result.TotalLines));

    private async Task<Outcome> RunAsync(string slug, ReferenceRequest request,
        CancellationToken cancellationToken)
    {
        string symbol = request.Symbol.Trim();
        if (request.Filter.Refusal is { } refused) return new Problem(refused);
        if (SearchQuery.Unusable(symbol, "find_references") is { } unusable) return new Problem(unusable);

        return await readers.OverIndexAsync(slug, request.Filter.Repository,
            (index, token) => QueryAsync(index, request, symbol, token), cancellationToken);
    }

    private static async Task<Outcome> QueryAsync(IndexReader index, ReferenceRequest request, string symbol,
        CancellationToken cancellationToken)
    {
        var connection = index.Connection;
        // The slug the index holds, not the one the caller typed: the filter's subquery matches it exactly.
        var filter = request.Filter with { Repository = index.Repository?.Slug };
        int maxFiles = Math.Clamp(request.MaxFiles, 1, MaxFiles);
        string pattern = SymbolText.WholeWordPattern(symbol);

        var fileParameters = new List<DuckDBParameter>();
        string fileFilter = filter.Sql(fileParameters);

        var matchParameters = new List<DuckDBParameter>
        {
            new("q", pattern), new("occurrence", SymbolText.OccurrencePattern(symbol))
        };
        string literally = SearchQuery.Literally(symbol, matchParameters);
        // What a match is, spelled once for the scan and for the coverage count, so the two cannot drift.
        string matches = $"{literally} AND regexp_matches(l.content, $q, '')";

        // Unlike grep this counts the unfiltered files whenever filters were given rather than only on
        // an empty answer: the footgun here is a result that looks complete because the file declaring
        // the symbol — a generated partial, most often — was excluded. The count is a superset of the
        // filtered answer, so both come out of one regex pass over `lines`: `matched` is every line
        // naming the symbol, and the filters are applied to it rather than to the scan (#173). It is
        // materialised because it is read twice, and a CTE inlined into both would be the two scans
        // this replaced. Without filters there is no second count, and the scan keeps its join. The
        // filter tail starts with `AND`, hence `WHERE true` in front of it.
        string hits = filter.Any
            ? $"""
               matched AS MATERIALIZED (
                   SELECT l.file_id, l.line_number, l.content FROM lines l
                   WHERE {matches}),
               hits AS (
                   SELECT m.file_id, m.line_number, m.content, f.qualified_path, f.extension
                   FROM matched m JOIN files f USING (file_id)
                   WHERE true{fileFilter}),
               """
            : $"""
               hits AS (
                   SELECT l.file_id, l.line_number, l.content, f.qualified_path, f.extension
                   FROM lines l JOIN files f USING (file_id)
                   WHERE {matches}),
               """;
        string everyFile = filter.Any ? ", (SELECT count(DISTINCT file_id) FROM matched) AS every_file" : "";

        // totals drives the join so that a symbol matching nothing still returns one row carrying the
        // zero counts, the way grep's page-past-the-end does.
        string sql = $"""
                      WITH {hits}
                      -- `occurrences` runs a pattern a second time over the hit lines, which the
                      -- first pass has already narrowed the table down to: a line naming the symbol
                      -- twice is two references, so counting rows would undercount the project total
                      -- the reply prints beside the sample. Measured on a 516 MB index with the \b
                      -- pattern it replaced, it added about 10 ms to a 51 ms scan of 151,000 matching
                      -- lines (#119) — cheap because it extracts from `hits` and never from `lines`;
                      -- the list_filter over one short list per line was not re-measured. Not $q, whose end boundary eats
                      -- the character the next occurrence starts on: a candidate counts when the word
                      -- character it captured after itself is none (SymbolText.OccurrencePattern).
                      per_file AS (
                          SELECT file_id, qualified_path, extension, count(*) AS n,
                                 sum(len(list_filter(regexp_extract_all(content, $occurrence, 1), v -> v = ''))) AS occurrences
                          FROM hits GROUP BY ALL),
                      totals AS (
                          SELECT count(*) AS total_files, coalesce(sum(n), 0) AS total_lines,
                                 coalesce(sum(occurrences), 0) AS total_occurrences FROM per_file),
                      page_files AS (
                          SELECT file_id, qualified_path, extension FROM per_file
                          ORDER BY n DESC, qualified_path
                          LIMIT $max_files),
                      kept AS (
                          SELECT file_id, qualified_path, extension, line_number, content FROM (
                              SELECT h.file_id, p.qualified_path, p.extension, h.line_number, h.content,
                                     row_number() OVER (PARTITION BY h.file_id ORDER BY h.line_number) AS rn
                              FROM hits h JOIN page_files p USING (file_id))
                          WHERE rn <= $max_lines)
                      SELECT t.total_files, t.total_lines, t.total_occurrences{everyFile},
                             k.file_id, k.qualified_path, k.extension, k.line_number, k.content
                      FROM totals t LEFT JOIN kept k ON true
                      ORDER BY k.qualified_path, k.line_number
                      """;

        var matched = new List<MatchedLine>();
        int totalFiles = 0;
        long totalLines = 0;
        long totalOccurrences = 0;
        int? withoutFilters = null;
        await using (var command = connection.Query(sql,
                         [
                             .. matchParameters, .. fileParameters, new("max_files", maxFiles),
                             new("max_lines", MaxLinesPerFile)
                         ]))
        await using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                totalFiles = (int)reader.Int64("total_files");
                totalLines = reader.Int64("total_lines");
                totalOccurrences = reader.Int64("total_occurrences");
                if (filter.Any) withoutFilters = (int)reader.Int64("every_file");
                if (reader.IsNull("qualified_path")) continue;
                matched.Add(new MatchedLine(reader.Int64("file_id"), reader.Text("qualified_path"),
                    Languages.Default.For(reader.Text("extension")), reader.Int32("line_number"),
                    reader.Text("content")));
            }
        }

        var scopes = await ScopesAsync(connection, matched, cancellationToken);
        // Where each matched line's file stands at the start of it, which is what keeps a line inside
        // a block comment opened forty lines up from reading as a call (#53).
        var positions = await FilePositions.ReadAsync(connection,
            matched.Select(line => (line.FileId, line.Analyzer, line.LineNumber)), cancellationToken);
        // Every appearance on the line, not only the first: a line naming the identifier twice is two
        // references, and they are often of different kinds. What each one looks like is the file's
        // language's question (ADR-0008), so `:=` is a write in an X# file and not in a C# one, and a
        // line inside a comment opened further up is a mention in every language.
        var references = matched
            .SelectMany(line => line.Analyzer
                .Occurrences(positions.GetValueOrDefault((line.FileId, line.LineNumber), FilePosition.Unknown),
                    line.Content, symbol)
                .Select(kind => new Reference(line.Path, line.LineNumber, line.Content, kind.Value,
                    scopes.TryGetValue(line.FileId, out var declarations)
                        ? DeclarationScope.Enclosing(declarations, line.LineNumber,
                            SymbolText.Indent(line.Content))
                        : null,
                    kind.Evidence)))
            .ToList();

        // Which of the files holding the name were classified with the default shapes rather than a
        // profile's (#126). Over every matching file and not only the page, because the caveat is
        // about what the answer covers, and the files past the cap are part of what it covers.
        // No shapeless languages to report: this search reads every file it matches whatever its
        // language, so a profile with no declaration shapes costs it nothing (#129).
        var extensions = await ScopeCoverage.ExtensionsAsync(connection, cancellationToken);
        var unprofiled = await ScopeCoverage.OfMatchesAsync(connection, extensions, matches, fileFilter,
            [.. matchParameters, .. fileParameters], [], cancellationToken);

        int filesExamined = matched.Select(line => line.FileId).Distinct().Count();
        return new ReferenceResult(totalFiles, filesExamined, totalLines, totalOccurrences, references,
            withoutFilters, unprofiled);
    }

    /// <summary>
    ///     One line the engine matched, before it is taken apart into the references on it. It carries
    ///     the analyser its file resolved to, so the lookup happens once per line rather than once for
    ///     the grouping and again for the classification.
    /// </summary>
    private readonly record struct MatchedLine(
        long FileId, string Path, ILanguageAnalyzer Analyzer, int LineNumber, string Content);

    /// <summary>
    ///     The declaration lines of every file a reference was read from, which is what an enclosing
    ///     scope is worked out from. DuckDB narrows each file to the few lines that could be a
    ///     declaration, so the whole file never leaves the index for the sake of a label on one line.
    ///     One query per language rather than one for all of them: what could be a declaration is the
    ///     analyser's pattern (ADR-0008) and a Delphi unit and a C# file do not share one. The grouping
    ///     is by analyser and not by extension, so the languages that spell declarations the same way
    ///     are still read in a single pass.
    /// </summary>
    private static async Task<Dictionary<long, List<DeclarationLine>>> ScopesAsync(
        DuckDBConnection connection, List<MatchedLine> matched, CancellationToken cancellationToken)
    {
        var scopes = new Dictionary<long, List<DeclarationLine>>();

        foreach (var group in matched.GroupBy(line => line.Analyzer))
        {
            long[] fileIds = [.. group.Select(line => line.FileId).Distinct()];
            if (fileIds.Length == 0) continue;

            // A language that declares nothing this can read is not asked for lines it would only
            // throw away; one that reads every line — a parser-backed analyser — narrows nothing.
            var parameters = new List<DuckDBParameter>();
            if (SearchQuery.Narrowing(group.Key.DeclarationCandidates, "d", parameters) is not { } narrowing)
                continue;

            // The ids came from the query above and never from the request, so inlining them is safe
            // and saves binding one parameter per file.
            string ids = string.Join(",", fileIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            await using var command = connection.Query($"""
                                                        SELECT file_id, line_number, content FROM lines
                                                        WHERE file_id IN ({ids}){narrowing}
                                                        ORDER BY file_id, line_number
                                                        """, parameters);
            await using var reader = await command.ReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string content = reader.Text("content");
                // Unknown and not the file's real position: a scope label needs the name a line
                // declares and never which side of a declaration/implementation split it is on, and
                // walking every file again to answer a question nothing here asks would double the
                // cost of every reference search. The role comes back null, which is what it means.
                if (group.Key.Declares(FilePosition.Unknown, content).Value is not { } declared) continue;
                long fileId = reader.Int64("file_id");
                if (!scopes.TryGetValue(fileId, out var declarations))
                    scopes[fileId] = declarations = [];
                declarations.Add(new DeclarationLine(reader.Int32("line_number"), SymbolText.Indent(content),
                    declared));
            }
        }

        return scopes;
    }

}
