using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     The reads an index answers that both a reader and a build make, as statements over a bare
///     connection with no lease and no project attached behind them.
///     They live here because the two callers cannot share an <see cref="IndexReader" />: a reader is
///     opened on an attached project, and a build holds a shadow index, which is a catalog nothing can
///     be opened on. A reader cannot serve the build for a second reason — it answers
///     <see cref="IndexReader.PathsAsync" /> from <c>index_info</c>, and a build writes that row last,
///     so mid-build the shape it would report is the wrong one. Hence the explicit
///     <see cref="ProjectPaths" /> parameter below rather than a reader that looks it up.
///     What matters is that neither caller writes this SQL. Two copies of the churn ranking would be
///     two definitions of what a project is busy with, and two copies of the extension counts would let
///     <c>list_extensions</c> and an overview describe one index differently — the worst case for an
///     agent that calls both.
/// </summary>
internal static class IndexQueries
{
    /// <summary>
    ///     What each extension accounts for, unordered: the caller ranks it, because a tool listing
    ///     extensions and an overview grouping them into languages sort on different things.
    /// </summary>
    /// <param name="connection">Bound to the index being counted: a live project, or a shadow being built.</param>
    /// <param name="repositorySlug">One repository, or null for every one in the project.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public static async Task<IReadOnlyList<ExtensionCount>> ExtensionCountsAsync(DuckDBConnection connection,
        string? repositorySlug, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        // The join is paid for only when there is a repository to scope to: every other caller counts
        // the whole project, and files.repo_id is the only thing repositories would contribute.
        string scope = "";
        if (repositorySlug is not null)
        {
            scope = " JOIN repositories r USING (repo_id) WHERE r.slug = $r";
            parameters.Add(new DuckDBParameter("r", repositorySlug));
        }

        using var command = connection.Query($"""
                                              SELECT f.extension,
                                                     count(*)::INTEGER AS files,
                                                     -- Cast because DuckDB widens sum of an INTEGER to
                                                     -- HUGEINT, which the driver hands back as a BigInteger.
                                                     sum(f.line_count)::BIGINT AS lines,
                                                     count(f.skip_reason)::INTEGER AS skipped
                                              FROM files f{scope}
                                              GROUP BY f.extension
                                              """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var counts = new List<ExtensionCount>();
        while (await reader.ReadAsync(cancellationToken))
            counts.Add(new ExtensionCount(reader.Text("extension"), reader.Int32("files"), reader.Int64("lines"),
                reader.Int32("skipped")));
        return counts;
    }

    /// <summary>
    ///     The window reaching <paramref name="days" /> back from the newest commit recorded in scope,
    ///     or null when the scope holds no commit at all — a repository whose history could not be
    ///     walked, which is not the same as one nobody changed and must not be answered as one.
    /// </summary>
    public static async Task<HistoryWindow?> WindowAsync(DuckDBConnection connection, int days,
        string? repositorySlug, CancellationToken cancellationToken)
    {
        var (scope, parameters) = CommitScope(repositorySlug);
        // epoch() for the reason StatusAsync gives: seconds as a double are the one representation of
        // a TIMESTAMPTZ that does not depend on whether ICU is loaded to decide the session time zone.
        using var command = connection.Query($"SELECT epoch(max(authored_at)) AS newest FROM commits {scope}",
            parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsNull("newest")) return null;
        return HistoryWindow.Ending(DateTimeOffset.FromUnixTimeSeconds((long)reader.Double("newest")), days);
    }

    /// <summary>
    ///     The files a window's commits touched, most commits first. The paths come back spelled the way
    ///     the project spells them, rather than each caller formatting the pair itself — the rule is
    ///     ADR-0006's and the caller passes the <see cref="ProjectPaths" /> that knows it.
    /// </summary>
    /// <param name="connection">Bound to the index being ranked: a live project, or a shadow being built.</param>
    /// <param name="paths">How this project names a file (ADR-0006).</param>
    /// <param name="window">The span to count over, inclusive at both ends.</param>
    /// <param name="repositorySlug">One repository, or null for every one in the project.</param>
    /// <param name="directoryInRepository">
    ///     A directory inside that repository to count under, or null for all of it. Meaningless
    ///     without <paramref name="repositorySlug" />, because a directory of one repository is not a
    ///     directory of another; callers resolve both from the one qualified path they were given.
    /// </param>
    /// <param name="limit">How many files to return.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public static async Task<IReadOnlyList<ChurnedFile>> RankAsync(DuckDBConnection connection, ProjectPaths paths,
        HistoryWindow window, string? repositorySlug, string? directoryInRepository, int limit,
        CancellationToken cancellationToken)
    {
        // The window is compared in epoch seconds rather than as a timestamp parameter, for the reason
        // WindowAsync reads it that way: it keeps the comparison off the session time zone, and it
        // keeps a DateTimeOffset out of the driver's parameter mapping entirely.
        var parameters = new List<DuckDBParameter>
        {
            new("since", window.Since.ToUnixTimeSeconds()),
            new("until", window.Until.ToUnixTimeSeconds())
        };
        var conditions = new List<string> { "epoch(c.authored_at) BETWEEN $since AND $until" };
        if (repositorySlug is not null)
        {
            conditions.Add("c.repo_slug = $r");
            parameters.Add(new DuckDBParameter("r", repositorySlug));
        }

        if (!string.IsNullOrEmpty(directoryInRepository))
        {
            // GLOB and not LIKE: it is this project's one glob dialect (ADR-0004, CODING_STANDARDS),
            // and `*` crosses `/` in it, so a single pattern covers every depth beneath the directory.
            conditions.Add("cf.path GLOB $d");
            parameters.Add(new DuckDBParameter("d", directoryInRepository.TrimEnd('/') + "/*"));
        }

        // Ranked first, and only then asked which of the survivors still exist. Resolving `at_head`
        // inside the aggregate would join `files` — the largest table here after `lines` — against
        // every path in the window, to answer a question about the twenty rows that outlive the LIMIT.
        // The semi-join below is paid per returned row instead of per window row.
        using var command = connection.Query($"""
                                              WITH ranked AS (
                                                  SELECT c.repo_slug, cf.path,
                                                         count(*)::INTEGER AS commits,
                                                         -- Cast for the reason ChangeLogAsync casts:
                                                         -- DuckDB widens sum of an INTEGER to HUGEINT,
                                                         -- which the driver hands back as a BigInteger.
                                                         sum(cf.added)::BIGINT AS added,
                                                         sum(cf.deleted)::BIGINT AS deleted
                                                  FROM commit_files cf
                                                  JOIN commits c USING (commit_id)
                                                  WHERE {string.Join(" AND ", conditions)}
                                                  GROUP BY c.repo_slug, cf.path
                                                  -- Spelled out rather than ordered by the aliases:
                                                  -- DuckDB resolves a bare name in ORDER BY against the
                                                  -- input columns first, so `added` would bind to
                                                  -- commit_files.added and the statement fail to bind.
                                                  ORDER BY count(*) DESC,
                                                           sum(cf.added) + sum(cf.deleted) DESC, cf.path
                                                  LIMIT {limit})
                                              SELECT ranked.*,
                                                     {AtHeadExists("ranked.repo_slug")} AS at_head
                                              FROM ranked
                                              ORDER BY commits DESC, added + deleted DESC, path
                                              """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var files = new List<ChurnedFile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            // Spelled from the repository and the path rather than read off a files row, because the
            // paths that have none are exactly the ones no longer at HEAD — and those are ranked and
            // must still be named.
            string slug = reader.Text("repo_slug");
            files.Add(new ChurnedFile(paths.Format(slug, reader.Text("path")), slug, reader.Flag("at_head"),
                reader.Int32("commits"), reader.Int64("added"), reader.Int64("deleted")));
        }

        return files;
    }

    /// <summary>
    ///     Whether a path a ranking returned is still a file at HEAD, as a SQL fragment over a
    ///     <c>ranked</c> CTE with a <c>path</c> column. Written once because both rankings in this
    ///     system rank paths a later commit deleted or renamed away, and a change to how "still there"
    ///     is recognised has to reach both or the two answer differently about one path.
    ///     Always applied to what survived a LIMIT, never inside the aggregate that produced it, for
    ///     the reason <see cref="RankAsync" /> gives where it uses this.
    /// </summary>
    /// <param name="repositorySlug">
    ///     The SQL naming the repository to look in: a column of <c>ranked</c> where the ranking spans
    ///     several, a parameter where it is scoped to one.
    /// </param>
    public static string AtHeadExists(string repositorySlug) =>
        $"""
         EXISTS (SELECT 1
                 FROM files f JOIN repositories hr USING (repo_id)
                 WHERE hr.slug = {repositorySlug}
                   AND f.path = ranked.path)
         """;

    /// <summary>
    ///     The WHERE clause and its parameter for one repository's commits, or neither for the
    ///     project's. Public because every read of the commit tables narrows the same way — the change
    ///     log and the commit count as much as the ranking — and two spellings of one clause is one of
    ///     them eventually being wrong.
    /// </summary>
    public static (string Scope, List<DuckDBParameter> Parameters) CommitScope(string? repositorySlug) =>
        repositorySlug is null
            ? ("", [])
            : ("WHERE repo_slug = $r", [new DuckDBParameter("r", repositorySlug)]);
}
