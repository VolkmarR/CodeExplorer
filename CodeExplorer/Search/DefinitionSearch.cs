using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>Everything a find_definition call asks for. Bounds are enforced by <see cref="DefinitionSearch" />.</summary>
/// <param name="Symbol">The identifier to look for, matched on word boundaries and case-sensitively.</param>
/// <param name="Filter">Which files to look in, the shape every index search takes (<see cref="FileFilter" />).</param>
public sealed record DefinitionRequest(string Symbol, FileFilter Filter);

/// <summary>
///     One place the symbol is declared: the line, what it declares there, and — where the language
///     separates the two — whether this is the announcement or the body.
///     <see cref="Role" /> is null where the language has no split and where it has one the scan could
///     not place the line in. Null rather than <see cref="DeclarationRole.Declaration" />, because the
///     announcement is the answer an agent is least often after and would win every tie.
/// </summary>
public sealed record DefinitionSite(
    string QualifiedPath,
    int LineNumber,
    string Text,
    string? Type,
    string? Member,
    DeclarationRole? Role,
    Evidence Evidence);

/// <summary>
///     Where a symbol is declared, and what to do when it is nowhere.
///     <see cref="FilesNamingIt" /> is filled only when there are no sites: "nothing declares this"
///     and "nothing spells this" send an agent to different places, and the second is a question about
///     the whole index that a declaration search has no reason to answer when it found something.
///     <see cref="Separated" /> is whether any of these sites came from a language that announces a
///     routine in one place and writes it in another. It is a fact about the languages read and not
///     about the sites found, which is the difference between "Delphi draws this distinction" and
///     "this particular answer happened to contain both" — a routine found only in an implementation
///     section is still an implementation, and a caller inferring the split from the sites would print
///     it unlabelled.
/// </summary>
public sealed record DefinitionResult(
    IReadOnlyList<DefinitionSite> Sites,
    int TotalSites,
    bool Separated,
    int FilesNamingIt,
    int? FilesNamingItWithoutFilters) : Outcome;

/// <summary>
///     Where a symbol is declared (#54), answered in one call instead of a reference search read past
///     its uses. The candidate set comes from DuckDB — the symbol on the line, and the line shaped
///     like a declaration in the language that file is written in (ADR-0008) — and the file's analyser
///     then says what the line declares and which side of a declaration/implementation split it sits
///     on. The same division as every other search here: the engine decides what matches, .NET decides
///     what a match is.
///     A miss degrades to <c>find_references</c> and to grep, which is why this can afford to be
///     heuristic: a declaration form a profile does not know costs a search the agent would have run
///     anyway, while a form invented to be thorough costs a call reported as a declaration. Every
///     reply says which it is.
/// </summary>
public sealed class DefinitionSearch(ProjectIndexes indexes)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell this apart from a reference scan.</summary>
    public const string Engine = "declaration scan";

    /// <summary>
    ///     How many declaration sites are reported. A symbol declared more than fifty times is a name
    ///     like <c>Id</c> or <c>Name</c> on fifty unrelated types, and the list is no longer the answer
    ///     to "where is this declared?"; the reply says how many there were and narrowing is what turns
    ///     it back into an answer.
    /// </summary>
    public const int MaxSites = 50;

    /// <summary>
    ///     How many candidate lines are read before they are placed. A candidate is already a line that
    ///     names the symbol <em>and</em> is shaped like a declaration, so this is generous by an order
    ///     of magnitude for anything but a name shared across a whole code base — where the sites the
    ///     cap cost would have been thrown away by <see cref="MaxSites" /> regardless.
    /// </summary>
    private const int MaxCandidates = 1_000;

    /// <summary>
    ///     Every definition search goes through here, which is what makes this the one place such a
    ///     search is recorded.
    /// </summary>
    public async Task<Outcome> FindAsync(string slug, DefinitionRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await RunAsync(slug, request, cancellationToken);
        if (outcome is DefinitionResult result)
            recording.Matched(Engine, result.Sites.Select(site => site.QualifiedPath).Distinct().Count(),
                result.TotalSites);
        else recording.Problem();
        return outcome;
    }

    private async Task<Outcome> RunAsync(string slug, DefinitionRequest request,
        CancellationToken cancellationToken)
    {
        string symbol = request.Symbol.Trim();
        if (SearchQuery.Unusable(symbol, "find_definition") is { } unusable) return new Problem(unusable);

        return await IndexReader.OverIndexAsync(indexes, slug, request.Filter.Repository,
            (index, token) => QueryAsync(index, request, symbol, token), cancellationToken);
    }

    private static async Task<Outcome> QueryAsync(IndexReader index, DefinitionRequest request, string symbol,
        CancellationToken cancellationToken)
    {
        var connection = index.Connection;
        // The slug the index holds, not the one the caller typed: the filter's subquery matches it exactly.
        var filter = request.Filter with { Repository = index.Repository?.Slug };
        var symbolPattern = new DuckDBParameter("q", SymbolText.WholeWordPattern(symbol));
        var parameters = new List<DuckDBParameter> { symbolPattern };
        string literally = SearchQuery.Literally(symbol, parameters);
        string declarationShapes = await ShapesAsync(connection, parameters, cancellationToken);
        string fileFilter = filter.Sql(parameters);

        var candidates = new List<Candidate>();
        using (var command = connection.Query($"""
                                                  SELECT f.file_id, f.qualified_path, f.extension,
                                                         l.line_number, l.content
                                                  FROM lines l JOIN files f USING (file_id)
                                                  WHERE {literally}
                                                    AND regexp_matches(l.content, $q, ''){fileFilter}
                                                    AND ({declarationShapes})
                                                  ORDER BY f.qualified_path, l.line_number
                                                  LIMIT {MaxCandidates}
                                                  """, parameters))
        using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add(new Candidate(reader.Int64("file_id"), reader.Text("qualified_path"),
                    Languages.Default.For(reader.Text("extension")), reader.Int32("line_number"),
                    reader.Text("content")));
        }

        // The lines above each candidate, so that a `procedure Foo;` inside a commented-out block is
        // not a declaration and one below a Delphi `implementation` is known to be the body.
        var positions = await FilePositions.ReadAsync(connection,
            candidates.Select(line => (line.FileId, line.Analyzer, line.LineNumber)), cancellationToken);

        var sites = new List<DefinitionSite>();
        // Whether the languages these sites were read from draw the split at all, which is what says
        // the answer may be labelled — not whether both labels happen to appear in it.
        bool separated = false;
        foreach (var candidate in candidates)
        {
            var position = positions.GetValueOrDefault((candidate.FileId, candidate.LineNumber),
                FilePosition.Unknown);
            var declared = candidate.Analyzer.Declares(position, candidate.Content);
            // The engine narrowed the line to one shaped like a declaration; this is what says the
            // shape declares the name asked about, rather than another name on the same line.
            if (declared.Value is not { } what) continue;
            if (!Names(what, symbol)) continue;
            if (SearchQuery.OnlyInProse(candidate.Analyzer, position, candidate.Content, symbol)) continue;
            separated |= candidate.Analyzer.SeparatesDeclarationFromImplementation;
            sites.Add(new DefinitionSite(candidate.Path, candidate.LineNumber, candidate.Content,
                what.Type, what.Member, what.Role, declared.Evidence));
        }

        // The body first: where a language announces a routine and then writes it, the announcement is
        // what an agent wanted least often and what sorts first everywhere else, because the spec file
        // is usually the one named first.
        var ranked = sites
            .OrderBy(site => site.Role switch
            {
                DeclarationRole.Implementation => 0,
                null => 1,
                _ => 2
            })
            .ThenBy(site => site.QualifiedPath, StringComparer.Ordinal)
            .ThenBy(site => site.LineNumber)
            .ToList();

        // Only when nothing was declared: an agent told "no declaration" needs to know whether the
        // name is spelled anywhere at all, because the two send it to different tools.
        int naming = 0;
        int? namingWithoutFilters = null;
        if (ranked.Count == 0)
        {
            // Both counts in one pass. The unfiltered one is a superset of the filtered one, so asking
            // twice is two scans of `lines` for one answer — and the second is only wanted at all
            // because a declaration hidden by a filter reads exactly like one that does not exist.
            using var command = connection.Query(
                $"""
                 SELECT count(DISTINCT l.file_id) FILTER (WHERE f.file_id IS NOT NULL) AS filtered,
                        count(DISTINCT l.file_id) AS every_file
                 FROM lines l LEFT JOIN (SELECT f.file_id FROM files f WHERE true{fileFilter}) f
                   USING (file_id)
                 WHERE {literally} AND regexp_matches(l.content, $q, '')
                 """, parameters);
            using var reader = await command.ReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                naming = (int)reader.Int64("filtered");
                if (filter.Any) namingWithoutFilters = (int)reader.Int64("every_file");
            }
        }

        return new DefinitionResult([.. ranked.Take(MaxSites)], ranked.Count, separated, naming,
            namingWithoutFilters);
    }

    /// <summary>One line the engine offered, before the file's language says what it declares.</summary>
    private readonly record struct Candidate(
        long FileId, string Path, ILanguageAnalyzer Analyzer, int LineNumber, string Content);

    /// <summary>Whether what the line declares is the symbol asked about, as a type or as a member.</summary>
    private static bool Names(Declared declared, string symbol) =>
        string.Equals(declared.Member, symbol, StringComparison.Ordinal)
        || string.Equals(declared.Type, symbol, StringComparison.Ordinal);

    /// <summary>
    ///     What a declaration looks like in each language this project is written in, as one
    ///     <c>WHERE</c> clause. A branch per analyser rather than one pattern for all of them: what
    ///     could be a declaration is the analyser's question (ADR-0008), a Delphi unit and a C# file do
    ///     not share an answer, and a language that declares nothing this can read contributes no
    ///     branch at all rather than lines that would be thrown away.
    ///     The extensions come from the index and not from the language table, so this asks about the
    ///     nine that are in the project rather than the forty that could be, and never has to name the
    ///     remainder that no profile covers — <see cref="LanguageRegistry.For" /> answers for those
    ///     like any other, which is what keeps the extension-to-language table on its own side of the
    ///     seam.
    /// </summary>
    private static async Task<string> ShapesAsync(DuckDBConnection connection, List<DuckDBParameter> parameters,
        CancellationToken cancellationToken)
    {
        var extensions = new List<string>();
        using (var command = connection.Query("SELECT DISTINCT extension FROM files", []))
        using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) extensions.Add(reader.Text("extension"));
        }

        var branches = new List<string>();
        foreach (var language in extensions.GroupBy(Languages.Default.For))
        {
            if (SearchQuery.Narrowing(language.Key.DeclarationCandidates, $"d{parameters.Count}", parameters)
                is not { } narrowing)
                continue;
            var names = new List<string>();
            foreach (string extension in language)
            {
                // Bound and not inlined: an extension is a value out of the index, whatever the table
                // that named its language says.
                string name = $"e{parameters.Count}";
                parameters.Add(new DuckDBParameter(name, extension));
                names.Add($"${name}");
            }

            branches.Add($"(f.extension IN ({string.Join(", ", names)}){narrowing})");
        }

        // An empty project, or one whose every language declares it can read no declarations, answers
        // nothing rather than everything — which is what an empty alternation would have meant.
        return branches.Count == 0 ? "false" : string.Join(" OR ", branches);
    }
}
