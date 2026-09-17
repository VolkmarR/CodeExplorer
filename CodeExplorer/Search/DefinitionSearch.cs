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
/// </summary>
public sealed record DefinitionResult(
    IReadOnlyList<DefinitionSite> Sites,
    int TotalSites,
    int FilesNamingIt,
    int? FilesNamingItWithoutFilters) : SearchOutcome;

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
    public async Task<SearchOutcome> FindAsync(string slug, DefinitionRequest request,
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

    private async Task<SearchOutcome> RunAsync(string slug, DefinitionRequest request,
        CancellationToken cancellationToken)
    {
        string symbol = request.Symbol.Trim();
        if (SearchQuery.Unusable(symbol, "find_definition") is { } unusable) return new SearchProblem(unusable);

        var open = await IndexReader.OpenAsync(indexes, slug, request.Filter.Repository, cancellationToken);
        if (open is IndexOpen.Refused refused) return new SearchProblem(refused.Explanation);
        using var index = ((IndexOpen.Opened)open).Reader;
        var connection = index.Connection;

        // The slug the index holds, not the one the caller typed: the filter's subquery matches it exactly.
        var filter = request.Filter with { Repository = index.Repository?.Slug };
        var symbolPattern = new DuckDBParameter("q", SymbolText.WholeWordPattern(symbol));
        var parameters = new List<DuckDBParameter> { symbolPattern };
        string declarationShapes = Shapes(parameters);
        string fileFilter = filter.Sql(parameters);

        var candidates = new List<Candidate>();
        using (var command = connection.Query($"""
                                                  SELECT f.file_id, f.qualified_path, f.extension,
                                                         l.line_number, l.content
                                                  FROM lines l JOIN files f USING (file_id)
                                                  WHERE regexp_matches(l.content, $q, ''){fileFilter}
                                                    AND ({declarationShapes})
                                                  ORDER BY f.qualified_path, l.line_number
                                                  LIMIT {MaxCandidates}
                                                  """, parameters))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
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
        foreach (var candidate in candidates)
        {
            var position = positions.GetValueOrDefault((candidate.FileId, candidate.LineNumber),
                FilePosition.Unknown);
            var declared = candidate.Analyzer.Declares(position, candidate.Content);
            // The engine narrowed the line to one shaped like a declaration; this is what says the
            // shape declares the name asked about, rather than another name on the same line.
            if (declared.Value is not { } what) continue;
            if (!Names(what, symbol)) continue;
            if (InProse(candidate, position, symbol)) continue;
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
            naming = (int)await connection.CountAsync(
                $"""
                 SELECT count(DISTINCT l.file_id) FROM lines l JOIN files f USING (file_id)
                 WHERE regexp_matches(l.content, $q, ''){fileFilter}
                 """, parameters, cancellationToken);
            if (filter.Any)
                namingWithoutFilters = (int)await connection.CountAsync(
                    "SELECT count(DISTINCT l.file_id) FROM lines l WHERE regexp_matches(l.content, $q, '')",
                    [symbolPattern], cancellationToken);
        }

        return new DefinitionResult([.. ranked.Take(MaxSites)], ranked.Count, naming, namingWithoutFilters);
    }

    /// <summary>One line the engine offered, before the file's language says what it declares.</summary>
    private readonly record struct Candidate(
        long FileId, string Path, ILanguageAnalyzer Analyzer, int LineNumber, string Content);

    /// <summary>Whether what the line declares is the symbol asked about, as a type or as a member.</summary>
    private static bool Names(Declared declared, string symbol) =>
        string.Equals(declared.Member, symbol, StringComparison.Ordinal)
        || string.Equals(declared.Type, symbol, StringComparison.Ordinal);

    /// <summary>
    ///     Whether the name sits in a comment or a string on this line, which the declaration patterns
    ///     cannot see: a commented-out <c>procedure Advance;</c> is shaped exactly like the live one.
    ///     A line the scan never reached is kept — unknown is not prose — because the alternative is
    ///     dropping the declaration of every symbol in a file too long to walk.
    /// </summary>
    private static bool InProse(Candidate candidate, FilePosition position, string symbol)
    {
        bool found = false;
        // Every appearance and not the first: a line that names the symbol in a trailing comment and
        // then declares it is a declaration, and reading only the first would lose it.
        foreach (int at in SymbolText.Occurrences(candidate.Content, symbol))
        {
            found = true;
            if (candidate.Analyzer.StateAt(position, candidate.Content, at).Value
                is not (Lexical.Comment or Lexical.Literal))
                return false;
        }

        return found;
    }

    /// <summary>
    ///     What a declaration looks like, per language, as one <c>WHERE</c> clause. A branch per
    ///     analyser rather than one pattern for all of them: what could be a declaration is the
    ///     analyser's question (ADR-0008), a Delphi unit and a C# file do not share an answer, and a
    ///     language that declares nothing this can read contributes no branch at all rather than lines
    ///     that would be thrown away.
    ///     The extensions are the registry's own constants and never a caller's, which is why they are
    ///     inlined where every value from a request is bound.
    /// </summary>
    private static string Shapes(List<DuckDBParameter> parameters)
    {
        var branches = new List<string>();
        var claimed = new List<string>();
        foreach (var claim in Languages.Default.Claims)
        {
            claimed.AddRange(claim.Extensions);
            if (SearchQuery.Narrowing(claim.Analyzer.DeclarationCandidates, $"d{parameters.Count}", parameters) is { } narrowing)
                branches.Add($"(f.extension IN ({List(claim.Extensions)}){narrowing})");
        }

        // Everything no profile covers, read by the fallback exactly as it was before any of this
        // existed. Left out, a project written in a language nobody declared would answer that nothing
        // in it is declared anywhere.
        if (SearchQuery.Narrowing(Languages.Default.Fallback.DeclarationCandidates, $"d{parameters.Count}", parameters) is { } rest)
            branches.Add($"(f.extension NOT IN ({List(claimed)}){rest})");

        // A build whose every analyser declares it can read no declarations answers nothing rather
        // than everything, which is what an empty alternation would have meant.
        return branches.Count == 0 ? "false" : string.Join(" OR ", branches);

        // Quoted rather than bound: an extension list is the registry's own constants, and binding one
        // parameter per extension of every language would be forty parameters for a clause that never
        // varies. The quote is doubled all the same — a registration is one `With` away from carrying
        // a caller's string, and the comment above would then be the only thing protecting the query.
        static string List(IEnumerable<string> extensions) =>
            string.Join(", ", extensions.Select(extension => "'" + Quoted(extension) + "'"));
    }

    private static string Quoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
