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
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

    /// <summary>
    ///     The files a window's commits changed alongside one file, most shared commits first, with
    ///     what the ceiling had to leave out to say it.
    ///     Pairing is anchored rather than a self-join of <c>commit_files</c> against itself. A
    ///     self-join is the obvious reading of "which files change together" and it is quadratic in the
    ///     size of every commit in the window at once: one vendor drop of five thousand paths is
    ///     twenty-five million pairs on its own, computed to answer a question about one file. Anchored,
    ///     the work is the anchor's own commits times what each of them touched, which the ceiling
    ///     bounds — so the cost of the query is bounded by the ceiling and not by the repository.
    ///     The ceiling is still needed for what it was needed for: one mass commit that happens to
    ///     touch the anchor pairs it with every path in the repository at once, and those pairs are not
    ///     coupling, they are one commit. Excluding them is the caller's to explain, which is why the
    ///     count of what was excluded comes back rather than being quietly dropped.
    /// </summary>
    /// <param name="connection">Bound to the index being read.</param>
    /// <param name="paths">How this project names a file (ADR-0006).</param>
    /// <param name="window">The span to pair over, inclusive at both ends.</param>
    /// <param name="repositorySlug">The anchor's repository. A commit touches one repository, so pairing never crosses one.</param>
    /// <param name="pathInRepository">The anchor file, repository-relative, as <c>commit_files</c> records it.</param>
    /// <param name="maxCommitPaths">The most paths a commit may touch and still be paired.</param>
    /// <param name="limit">How many co-changed files to return.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public static async Task<CoChanges> CoChangedAsync(DuckDBConnection connection, ProjectPaths paths,
        HistoryWindow window, string repositorySlug, string pathInRepository, int maxCommitPaths, int limit,
        CancellationToken cancellationToken)
    {
        // Epoch seconds rather than timestamp parameters, for the reason RankAsync compares them that
        // way: it keeps the comparison off the session time zone and a DateTimeOffset out of the driver.
        var parameters = new List<DuckDBParameter>
        {
            new("since", window.Since.ToUnixTimeSeconds()),
            new("until", window.Until.ToUnixTimeSeconds()),
            new("r", repositorySlug),
            new("p", pathInRepository),
            new("c", maxCommitPaths)
        };

        using var command = connection.Query($"""
                                              -- The anchor's own commits first, and everything after
                                              -- reads only those. Narrowing here rather than later is
                                              -- what keeps the work proportional to one file's history
                                              -- instead of to the whole window's.
                                              WITH anchor AS (
                                                  SELECT cf.commit_id
                                                  FROM commit_files cf
                                                  JOIN commits c USING (commit_id)
                                                  WHERE c.repo_slug = $r
                                                    AND epoch(c.authored_at) BETWEEN $since AND $until
                                                    AND cf.path = $p),
                                              -- Every path those commits touched. The window and the
                                              -- repository are not repeated: a commit_id from anchor
                                              -- already satisfies both.
                                              touched AS (
                                                  SELECT cf.commit_id, cf.path
                                                  FROM commit_files cf JOIN anchor USING (commit_id)),
                                              sized AS (
                                                  SELECT commit_id,
                                                         count(*) <= $c AS paired
                                                  FROM touched GROUP BY commit_id),
                                              -- One row per commit that touched the anchor, so this
                                              -- counts commits and not paths.
                                              counts AS (
                                                  SELECT count(*)::INTEGER AS commits,
                                                         count(*) FILTER (WHERE paired)::INTEGER AS paired
                                                  FROM sized),
                                              ranked AS (
                                                  SELECT t.path, count(*)::INTEGER AS shared
                                                  FROM touched t JOIN sized s USING (commit_id)
                                                  WHERE s.paired AND t.path <> $p
                                                  GROUP BY t.path
                                                  -- Spelled out rather than ordered by the alias, for
                                                  -- the reason RankAsync spells its ORDER BY out.
                                                  ORDER BY count(*) DESC, t.path
                                                  -- Inlined and not parameterised: it is an int the
                                                  -- caller has already clamped to a range, so there is
                                                  -- nothing to escape, and RankAsync inlines its own
                                                  -- the same way.
                                                  LIMIT {limit})
                                              -- LEFT JOIN ON TRUE so the counts survive an empty
                                              -- ranking: a file that moves alone still has to say how
                                              -- many commits it was looked at over.
                                              SELECT counts.commits, counts.paired, ranked.path, ranked.shared,
                                                     EXISTS (SELECT 1
                                                             FROM files f JOIN repositories rp USING (repo_id)
                                                             WHERE rp.slug = $r
                                                               AND f.path = ranked.path) AS at_head
                                              FROM counts LEFT JOIN ranked ON TRUE
                                              ORDER BY ranked.shared DESC, ranked.path
                                              """, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var files = new List<CoChangedFile>();
        int commits = 0, paired = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            commits = reader.Int32("commits");
            paired = reader.Int32("paired");
            // Null where the join found no co-changed file at all, which is one row and not none.
            if (reader.IsNull("path")) continue;
            files.Add(new CoChangedFile(paths.Format(repositorySlug, reader.Text("path")), reader.Flag("at_head"),
                reader.Int32("shared")));
        }

        return new CoChanges(commits, paired, files);
    }

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
