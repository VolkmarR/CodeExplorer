using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>Everything a list_matches call asks for. Bounds are enforced by <see cref="MatchList" />.</summary>
/// <param name="Query">An RE2 pattern. Always a pattern: there is nothing to deduplicate about a literal.</param>
/// <param name="Filter">Which files to search, the shape every index search takes (<see cref="FileFilter" />).</param>
/// <param name="Group">Which capture group to collect, or 0 for the whole match.</param>
/// <param name="CaseSensitive">Whether the pattern matches case exactly.</param>
/// <param name="WholeWord">Anchors the pattern on word boundaries.</param>
/// <param name="Limit">How many distinct values to return, most frequent first.</param>
public sealed record MatchListRequest(
    string Query,
    FileFilter Filter,
    int Group = 0,
    bool CaseSensitive = false,
    bool WholeWord = false,
    int Limit = MatchList.DefaultLimit);

/// <summary>
///     One distinct value: how many times it was matched, and in how many files. The two differ
///     whenever a value repeats within a file, which is what tells a constant used everywhere from one
///     used twenty times in one place.
/// </summary>
public sealed record DistinctMatch(string Value, long Count, int Files);

/// <summary>
///     Either a <see cref="MatchListResult" /> or a <see cref="MatchListProblem" />: a semantic
///     failure is an answer, never an exception.
/// </summary>
public abstract record MatchListOutcome;

/// <summary>What went wrong and what to try instead, in agent-facing prose.</summary>
public sealed record MatchListProblem(string Explanation) : MatchListOutcome;

/// <summary>
///     The distinct values, most frequent first. <see cref="TotalDistinct" /> counts every value and
///     <see cref="Matches" /> the first <c>limit</c> of them, so a caller can say what it is not
///     showing. <see cref="MatchesWithoutFilters" /> is filled only when nothing matched under
///     filters: "this pattern matches nothing" and "your filters hid every value" read identically
///     and mean opposite things.
/// </summary>
public sealed record MatchListResult(
    int TotalDistinct,
    long TotalMatches,
    int TotalFiles,
    IReadOnlyList<DistinctMatch> Matches,
    long? MatchesWithoutFilters) : MatchListOutcome;

/// <summary>
///     The distinct values a pattern matches across a project — the indexed equivalent of
///     <c>grep -o … | sort | uniq -c</c>, and the tool for "what values exist?" rather than "where is
///     this?". Extraction is <c>regexp_extract_all</c> with the capture-group index and the
///     deduplication is a <c>GROUP BY</c>, so the whole answer is computed in DuckDB (ADR-0004) and
///     only the distinct values cross the wire. Nothing here opens <c>control.duckdb</c> (ADR-0005).
/// </summary>
public sealed class MatchList(ProjectIndexes indexes)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell this apart from a grep.</summary>
    public const string Engine = "match list";

    /// <summary>
    ///     Two hundred values is a vocabulary an agent can read; past it the answer is a data dump and
    ///     the right next move is a narrower pattern, which the reply says.
    /// </summary>
    public const int DefaultLimit = 200;

    /// <summary>
    ///     A thousand short values is roughly the reply cap, so a higher ceiling would only be
    ///     truncated; narrowing the pattern is the honest way to see more.
    /// </summary>
    public const int MaxLimit = 1000;

    /// <summary>
    ///     RE2 numbers groups from 1 and stops well before this; the ceiling exists so the value can be
    ///     inlined into the SQL as the literal <c>regexp_extract_all</c> requires.
    /// </summary>
    public const int MaxGroup = 9;

    /// <summary>
    ///     Every match listing goes through here, which is what makes this the one place such a search
    ///     is recorded. It is the only public method for the same reason grep has one: a second entry
    ///     point has nothing else to call.
    /// </summary>
    public async Task<MatchListOutcome> ListAsync(string slug, MatchListRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await RunAsync(slug, request, cancellationToken);
        if (outcome is MatchListResult result) recording.Matched(Engine, result.TotalFiles, result.TotalMatches);
        else recording.Problem();
        return outcome;
    }

    private async Task<MatchListOutcome> RunAsync(string slug, MatchListRequest request,
        CancellationToken cancellationToken)
    {
        string query = request.Query.Trim();
        if (query.Length == 0)
            return new MatchListProblem(
                "The pattern is empty. Pass an RE2 pattern with parentheses around the part you want, "
                + "such as \"PackageReference Include=\\\"([^\\\"]+)\\\"\" with group=1.");
        if (Re2.Unsupported(query) is { } unsupported) return new MatchListProblem(unsupported);

        if (request.Group is < 0 or > MaxGroup)
            return new MatchListProblem(
                $"group={request.Group} is out of range; it must be 0 for the whole match or 1-{MaxGroup} for a capture group.");

        // Checked here rather than left to the engine: DuckDB reports a missing group as an invalid
        // argument, which reads as a broken pattern and sends the caller off fixing a parenthesis
        // that was never wrong.
        int groups = Re2.CaptureGroups(query);
        if (request.Group > groups)
            return new MatchListProblem(
                $"The pattern has {(groups == 0 ? "no capture groups" : $"only {groups} capture {ToolReply.Plural(groups, "group")}")}, so group={request.Group} cannot be extracted. "
                + "Put parentheses around the part that varies, or use group=0 for the whole match.");

        // Non-capturing, so the group numbers the caller passed still mean what they meant.
        if (request.WholeWord) query = $@"\b(?:{query})\b";

        using var lease = await indexes.OpenAsync(slug, cancellationToken);
        if (lease is null) return new MatchListProblem(ToolReply.NoIndex(slug));
        var connection = lease.Connection;

        (string? repository, string? unknown) =
            await FileFilter.ResolveRepositoryAsync(connection, slug, request.Filter.Repository, cancellationToken);
        if (unknown is not null) return new MatchListProblem($"{unknown} Drop `repo` to search all of them.");

        var filter = request.Filter with { Repository = repository };
        int limit = Math.Clamp(request.Limit, 1, MaxLimit);

        var matchParameters = new List<DuckDBParameter>
        {
            new("q", query),
            new("flags", request.CaseSensitive ? "" : "i")
        };
        var fileParameters = new List<DuckDBParameter>();
        string fileFilter = filter.Sql(fileParameters);

        try
        {
            var matches = new List<DistinctMatch>();
            int totalDistinct = 0;
            long totalMatches = 0;
            int totalFiles = 0;

            // regexp_matches narrows to the lines that match before regexp_extract_all runs over them,
            // which is the difference between extracting from a project and extracting from its hits.
            // unnest flattens the per-line array, so a line matching three times contributes three
            // rows, the way `grep -o` emits one line per match. The group index is inlined because
            // regexp_extract_all takes it as a literal; it is bounded above, so it is a digit.
            using (var command = connection.Query($"""
                                                      WITH extracted AS (
                                                          SELECT l.file_id,
                                                                 unnest(regexp_extract_all(l.content, $q, {request.Group}, $flags)) AS value
                                                          FROM lines l JOIN files f USING (file_id)
                                                          WHERE regexp_matches(l.content, $q, $flags){fileFilter}),
                                                      grouped AS (
                                                          SELECT value, count(*) AS n, count(DISTINCT file_id) AS files
                                                          FROM extracted WHERE value <> '' GROUP BY value),
                                                      totals AS (
                                                          SELECT count(*) AS total_distinct, coalesce(sum(n), 0) AS total_matches,
                                                                 -- The same WHERE the grouping uses: a file whose capture
                                                                 -- group came back empty every time contributed no value
                                                                 -- and must not be counted as a file one came from.
                                                                 (SELECT count(DISTINCT file_id) FROM extracted WHERE value <> '') AS total_files
                                                          FROM grouped)
                                                      SELECT g.value, g.n, g.files, t.total_distinct, t.total_matches, t.total_files
                                                      FROM grouped g CROSS JOIN totals t
                                                      ORDER BY g.n DESC, g.value
                                                      LIMIT {limit}
                                                      """, [.. matchParameters, .. fileParameters]))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    matches.Add(new DistinctMatch(reader.Text("value"), reader.Int64("n"),
                        (int)reader.Int64("files")));
                    totalDistinct = (int)reader.Int64("total_distinct");
                    totalMatches = reader.Int64("total_matches");
                    totalFiles = (int)reader.Int64("total_files");
                }
            }

            long? withoutFilters = null;
            if (totalDistinct == 0 && filter.Any)
                // Same pattern, no file filters: a second pass only on the empty answer, so the common
                // case pays nothing.
                withoutFilters = await connection.CountAsync(
                    "SELECT count(*) FROM lines l WHERE regexp_matches(l.content, $q, $flags)",
                    matchParameters, cancellationToken);

            return new MatchListResult(totalDistinct, totalMatches, totalFiles, matches, withoutFilters);
        }
        catch (DuckDBException ex) when (Re2.IsPatternRejection(ex))
        {
            // The pattern is the only caller text a parser sees here; anything else DuckDB raises is
            // infrastructure and propagates.
            return new MatchListProblem(Re2.Rejected(ex));
        }
    }

}
