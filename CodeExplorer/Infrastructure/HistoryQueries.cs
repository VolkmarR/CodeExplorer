using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     The churn ranking (CONTEXT.md, Churn) as two statements over a connection, with no lease and no
///     project attached behind them. It is spelled out here rather than only on
///     <see cref="IndexReader" /> because the index build ranks a project's churn too, into the
///     overview it stores — and a build holds a shadow index, which is a catalog no reader can be
///     opened on. Two copies of this ranking would be two definitions of what a project is busy with,
///     and the one on the overview page is the one nobody would think to check against the tool.
///     Both callers therefore ask this; neither writes the SQL.
/// </summary>
internal static class HistoryQueries
{
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
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
                                                     EXISTS (SELECT 1
                                                             FROM files f JOIN repositories r USING (repo_id)
                                                             WHERE r.slug = ranked.repo_slug
                                                               AND f.path = ranked.path) AS at_head
                                              FROM ranked
                                              ORDER BY commits DESC, added + deleted DESC, path
                                              """, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

    /// <summary>The WHERE clause and its parameter for one repository's commits, or neither for the project's.</summary>
    private static (string Scope, List<DuckDBParameter> Parameters) CommitScope(string? repositorySlug) =>
        repositorySlug is null
            ? ("", [])
            : ("WHERE repo_slug = $r", [new DuckDBParameter("r", repositorySlug)]);
}
