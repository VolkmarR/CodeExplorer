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
        var (conditions, parameters) = ChurnScope(window, repositorySlug, directoryInRepository);

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
    ///     The same window's churn rolled up to the directories <paramref name="depth" /> segments
    ///     beneath the scope, most commits first. A rollup rather than a ranking the caller sums
    ///     itself, because a directory's churn is distinct commits and not the sum over its files
    ///     (CONTEXT.md, Churn) — the two disagree by however many files one commit touched.
    ///     A path with fewer segments than the depth is its own row, which keeps a file sitting
    ///     directly in the scope counted somewhere and named as what it is.
    /// </summary>
    /// <param name="connection">Bound to the index being ranked.</param>
    /// <param name="paths">How this project names a file (ADR-0006).</param>
    /// <param name="window">The span to count over, inclusive at both ends.</param>
    /// <param name="repositorySlug">One repository, or null for every one in the project.</param>
    /// <param name="directoryInRepository">A directory inside that repository, or null for all of it.</param>
    /// <param name="depth">
    ///     Segments beneath the scope to group by, at least one. In a multi-repository project scoped
    ///     to nothing, the first segment of a qualified path is the repository, so one level of the
    ///     depth is spent there and a depth of one ranks repositories.
    /// </param>
    /// <param name="limit">How many directories to return.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public static async Task<IReadOnlyList<ChurnedFile>> RankDirectoriesAsync(DuckDBConnection connection,
        ProjectPaths paths, HistoryWindow window, string? repositorySlug, string? directoryInRepository, int depth,
        int limit, CancellationToken cancellationToken)
    {
        var (conditions, parameters) = ChurnScope(window, repositorySlug, directoryInRepository);
        string prefix = directoryInRepository?.TrimEnd('/') ?? "";
        parameters.Add(new DuckDBParameter("p", prefix));
        // The repository slug leads a qualified path only where the project puts it there (ADR-0006),
        // and where it does it is the first segment an agent counts. Spending a level on it here is
        // what makes one depth mean one thing: the first N segments of the path the agent was shown.
        int beneathScope = depth - (repositorySlug is null && !paths.SingleRepository ? 1 : 0);
        parameters.Add(new DuckDBParameter("n", Math.Max(beneathScope, 0)));

        using var command = connection.Query($"""
                                              WITH touched AS (
                                                  SELECT c.repo_slug, cf.commit_id, cf.added, cf.deleted,
                                                         -- The scope is already matched by the WHERE
                                                         -- clause, so every path here starts with it;
                                                         -- +2 steps over the prefix and its slash.
                                                         string_split(CASE WHEN $p = '' THEN cf.path
                                                                           ELSE substr(cf.path, length($p) + 2)
                                                                      END, '/') AS segments
                                                  FROM commit_files cf
                                                  JOIN commits c USING (commit_id)
                                                  WHERE {string.Join(" AND ", conditions)}
                                              ),
                                              grouped AS (
                                                  SELECT repo_slug,
                                                         -- list_slice past the end returns the whole
                                                         -- list, which is how a path shallower than the
                                                         -- depth becomes its own row rather than none.
                                                         array_to_string(list_slice(segments, 1, $n), '/') AS below,
                                                         -- DISTINCT and not count(*): a commit that
                                                         -- touched forty files under this directory
                                                         -- changed this directory once.
                                                         count(DISTINCT commit_id)::INTEGER AS commits,
                                                         -- Cast for the reason RankAsync casts.
                                                         sum(added)::BIGINT AS added,
                                                         sum(deleted)::BIGINT AS deleted
                                                  FROM touched
                                                  GROUP BY repo_slug, below
                                                  ORDER BY count(DISTINCT commit_id) DESC,
                                                           sum(added) + sum(deleted) DESC, below
                                                  -- Inlined rather than parameterised, and safe
                                                  -- because it is an int the caller has already
                                                  -- clamped; RankAsync inlines its own the same way.
                                                  LIMIT {limit}
                                              ),
                                              ranked AS (
                                                  SELECT grouped.* EXCLUDE (below),
                                                         CASE WHEN $p = '' THEN below
                                                              WHEN below = '' THEN $p
                                                              ELSE $p || '/' || below
                                                         END AS path
                                                  FROM grouped
                                              )
                                              SELECT ranked.*,
                                                     -- The empty path is the repository's own root,
                                                     -- which is there as long as the repository is.
                                                     (ranked.path = ''
                                                      OR {AtHeadExists("ranked.repo_slug", true)}) AS at_head
                                              FROM ranked
                                              ORDER BY commits DESC, added + deleted DESC, path
                                              """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var directories = new List<ChurnedFile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.Text("repo_slug");
            directories.Add(new ChurnedFile(paths.Format(slug, reader.Text("path")), slug, reader.Flag("at_head"),
                reader.Int32("commits"), reader.Int64("added"), reader.Int64("deleted")));
        }

        return directories;
    }

    /// <summary>
    ///     The window and the scope both rankings of churn count over, as conditions over
    ///     <c>commit_files cf</c> joined to <c>commits c</c>. Written once because the file ranking and
    ///     the directory rollup answer the same question at two grains, and a scope that meant
    ///     something different in one of them would have the rollup disagree with the files under it.
    /// </summary>
    private static (List<string> Conditions, List<DuckDBParameter> Parameters) ChurnScope(HistoryWindow window,
        string? repositorySlug, string? directoryInRepository)
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

        return (conditions, parameters);
    }

    /// <summary>
    ///     Whether a path a ranking returned is still there at HEAD, as a SQL fragment over a
    ///     <c>ranked</c> CTE with a <c>path</c> column. Written once because every ranking in this
    ///     system ranks paths a later commit deleted or renamed away, and a change to how "still there"
    ///     is recognised has to reach all of them or two answer differently about one path.
    ///     Always applied to what survived a LIMIT, never inside the aggregate that produced it, for
    ///     the reason <see cref="RankAsync" /> gives where it uses this.
    /// </summary>
    /// <param name="repositorySlug">
    ///     The SQL naming the repository to look in: a column of <c>ranked</c> where the ranking spans
    ///     several, a parameter where it is scoped to one.
    /// </param>
    /// <param name="includingBeneath">
    ///     True where the ranked path may be a directory, which is there at HEAD while anything under
    ///     it is. The rollup asks it of rows that can be either — a row shallower than the depth it was
    ///     grouped by is a file — so the two tests are or-ed rather than chosen between.
    /// </param>
    public static string AtHeadExists(string repositorySlug, bool includingBeneath = false) =>
        $"""
         EXISTS (SELECT 1
                 FROM files f JOIN repositories hr USING (repo_id)
                 WHERE hr.slug = {repositorySlug}
                   AND (f.path = ranked.path
                        {(includingBeneath ? "OR f.path GLOB ranked.path || '/*'" : "")}))
         """;

    /// <summary>
    ///     The WHERE clause and its parameter for one repository's commits, or neither for the
    ///     project's. Public because every read of the commit tables narrows the same way — the change
    ///     log and the commit count as much as the ranking — and two spellings of one clause is one of
    ///     them eventually being wrong.
    /// </summary>
    public static (string Scope, List<DuckDBParameter> Parameters) CommitScope(string? repositorySlug) =>
        CommitScope(repositorySlug, null);

    /// <summary>
    ///     The same, narrowed to the commits of one author as well. The author is matched on the
    ///     address and never the display name: the address is the identity git records, it is what the
    ///     overview groups authors by, and it is stable where a name is not — one person commits as
    ///     "Grace Hopper", "grace" and "Grace M. Hopper" from one address, and two people share a
    ///     first name. Matching both would make the same call mean different things depending on which
    ///     spelling a commit happened to carry.
    ///     A case-insensitive substring, because an agent has an address it read off an overview or a
    ///     blame, and a local part ("grace") is the half of it worth typing. A substring can still
    ///     match two addresses, so what it matched is named in the reply rather than assumed
    ///     (<see cref="HistoryQueries.LogAsync" />).
    ///     The pattern is escaped and bound, never interpolated: it is caller text, and `%` or `_` in
    ///     it would otherwise widen the match silently.
    /// </summary>
    /// <summary>
    ///     The same again, narrowed to the commits whose subject carries some text.
    ///     The subject and not the body: an identifier that brings an agent here — a ticket key, a PR
    ///     number, a release name — is put in the subject line by every convention that puts it in a
    ///     commit at all, and matching the body as well would make a passing mention in a paragraph
    ///     rank equal with the commit that declares itself. Said in the tool's own words, so a caller
    ///     that meant the body knows this did not search it.
    ///     A case-insensitive substring for the reason the address is one, and escaped and bound for
    ///     the same reason too: this is caller text, and a `%` in it would widen the match rather than
    ///     fail to find one.
    /// </summary>
    public static (string Scope, List<DuckDBParameter> Parameters) CommitScope(string? repositorySlug, string? author,
        string? message = null)
    {
        var clauses = new List<string>(3);
        var parameters = new List<DuckDBParameter>(3);
        if (repositorySlug is not null)
        {
            clauses.Add("repo_slug = $r");
            parameters.Add(new DuckDBParameter("r", repositorySlug));
        }

        if (author is not null)
        {
            // ESCAPE '!' rather than the backslash default: caller text carries backslashes of its
            // own, and '!' is not a character of an address.
            clauses.Add("author_email ILIKE $a ESCAPE '!'");
            parameters.Add(new DuckDBParameter("a", $"%{Escaped(author)}%"));
        }

        if (message is not null)
        {
            clauses.Add("subject ILIKE $m ESCAPE '!'");
            parameters.Add(new DuckDBParameter("m", $"%{Escaped(message)}%"));
        }

        return (clauses.Count == 0 ? "" : $"WHERE {string.Join(" AND ", clauses)}", parameters);
    }

    /// <summary>The LIKE metacharacters, made literal, so caller text matches as the text it is.</summary>
    private static string Escaped(string text) =>
        text.Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal);
}
