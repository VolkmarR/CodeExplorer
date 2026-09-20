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
    /// <param name="filters">
    ///     Which extensions to rank and which paths to drop, in <see cref="PathTerms" />' syntax, or
    ///     <see cref="ChurnFilters.None" /> to rank everything the scope holds.
    /// </param>
    /// <param name="limit">How many files to return.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public static async Task<IReadOnlyList<ChurnedFile>> RankAsync(DuckDBConnection connection, ProjectPaths paths,
        HistoryWindow window, string? repositorySlug, string? directoryInRepository, ChurnFilters filters, int limit,
        CancellationToken cancellationToken)
    {
        var (conditions, parameters) = ChurnScope(paths, window, repositorySlug, directoryInRepository, filters);

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
    /// <param name="filters">
    ///     The same filters the file ranking takes, applied to the files before they are rolled up — so
    ///     a directory's churn is the churn of the files the caller can still see.
    /// </param>
    /// <param name="limit">How many directories to return.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public static async Task<IReadOnlyList<ChurnedFile>> RankDirectoriesAsync(DuckDBConnection connection,
        ProjectPaths paths, HistoryWindow window, string? repositorySlug, string? directoryInRepository, int depth,
        ChurnFilters filters, int limit, CancellationToken cancellationToken)
    {
        var (conditions, parameters) = ChurnScope(paths, window, repositorySlug, directoryInRepository, filters);
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
    ///     How many distinct paths of the scope the filters kept out of a ranking. Counted rather than
    ///     implied: a filtered ranking reads exactly like an unfiltered one, and "the top file here is
    ///     X" is a sentence an agent repeats. Asked only where something was filtered, so an
    ///     unfiltered call pays for nothing.
    ///     One number for both filters and not one each (#161). A reader wants to know how much of the
    ///     window is off screen, and splitting that into "excluded" and "the wrong extension" invites
    ///     adding the two — which double-counts every path both filters would have dropped.
    /// </summary>
    public static async Task<int> HiddenAsync(DuckDBConnection connection, ProjectPaths paths,
        HistoryWindow window, string? repositorySlug, string? directoryInRepository, ChurnFilters filters,
        CancellationToken cancellationToken)
    {
        if (!filters.Any) return 0;

        // The scope without the filters, and then only the paths they drop: the same window and the
        // same directory, so the number is about this ranking and not about the project.
        var (conditions, parameters) = ChurnScope(paths, window, repositorySlug, directoryInRepository,
            ChurnFilters.None);
        string path = ChurnedPath(paths);
        var dropped = new List<string>(2);
        if (PathTerms.Excluding(filters.Exclude, path, "cx", parameters) is { } excluding)
            dropped.Add($"NOT ({excluding})");
        if (PathTerms.Including(filters.Extensions, path, "ce", parameters) is { } including)
            dropped.Add($"NOT ({including})");
        // OR and not AND: a path is off screen if EITHER filter drops it, and the DISTINCT below is
        // what keeps one dropped by both from being counted twice.
        conditions.Add($"({string.Join(" OR ", dropped)})");

        using var command = connection.Query($"""
                                              SELECT count(*) AS hidden FROM (
                                                  SELECT DISTINCT c.repo_slug, cf.path
                                                  FROM commit_files cf JOIN commits c USING (commit_id)
                                                  WHERE {string.Join(" AND ", conditions)})
                                              """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (int)reader.Int64("hidden") : 0;
    }

    /// <summary>
    ///     Which extensions the window's scope actually holds, most-changed first, so a caller can be
    ///     offered the filter rather than made to guess at it (#161). Counted over the same window and
    ///     scope as the ranking and BEFORE the extension filter, because a list that narrowed with the
    ///     selection would delete the option a reader needs to get back out of it — but after
    ///     <c>exclude</c>, which says what is not code here at all.
    ///     The extension is read off the last segment's last dot, so a dotfile with no extension and a
    ///     path with a dot in a directory name both fall to the empty string, which is what a file with
    ///     no extension is. It is ranked by commits like the files are, which is what puts <c>.cs</c>
    ///     above <c>.xlf</c> in the list a reader opens even when the translations outnumber it.
    /// </summary>
    public static async Task<IReadOnlyList<ChurnedExtension>> ChurnedExtensionsAsync(DuckDBConnection connection,
        ProjectPaths paths, HistoryWindow window, string? repositorySlug, string? directoryInRepository,
        string? exclude, int limit, CancellationToken cancellationToken)
    {
        var (conditions, parameters) = ChurnScope(paths, window, repositorySlug, directoryInRepository,
            new ChurnFilters(null, exclude));
        parameters.Add(new DuckDBParameter("xl", limit));

        using var command = connection.Query($"""
                                              WITH named AS (
                                                  SELECT c.repo_slug, cf.path, cf.commit_id,
                                                         -- The last segment, so a dot in a directory
                                                         -- name is not read as an extension.
                                                         split_part(cf.path, '/', -1) AS leaf
                                                  FROM commit_files cf
                                                  JOIN commits c USING (commit_id)
                                                  WHERE {string.Join(" AND ", conditions)}
                                              )
                                              SELECT CASE WHEN contains(leaf, '.')
                                                          THEN lower('.' || split_part(leaf, '.', -1))
                                                          ELSE '' END AS extension,
                                                     count(DISTINCT commit_id)::INTEGER AS commits,
                                                     count(DISTINCT (repo_slug, path))::INTEGER AS files
                                              FROM named
                                              GROUP BY extension
                                              ORDER BY commits DESC, files DESC, extension
                                              LIMIT $xl
                                              """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var extensions = new List<ChurnedExtension>();
        while (await reader.ReadAsync(cancellationToken))
            extensions.Add(new ChurnedExtension(reader.Text("extension"), reader.Int32("commits"),
                reader.Int32("files")));

        return extensions;
    }

    /// <summary>
    ///     The window and the scope both rankings of churn count over, as conditions over
    ///     <c>commit_files cf</c> joined to <c>commits c</c>. Written once because the file ranking and
    ///     the directory rollup answer the same question at two grains, and a scope that meant
    ///     something different in one of them would have the rollup disagree with the files under it.
    /// </summary>
    private static (List<string> Conditions, List<DuckDBParameter> Parameters) ChurnScope(ProjectPaths paths,
        HistoryWindow window, string? repositorySlug, string? directoryInRepository, ChurnFilters filters)
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
            // starts_with and not GLOB, for the reason AtHeadExists gives: this scope is a directory
            // NAME and not a pattern. `[slug]`, `[id]` and `[...rest]` are route directories in every
            // file-system router, and each of them is a character class to GLOB — `app/[slug]` would
            // scope to whatever `app/s`, `app/l`, `app/u` and `app/g` hold and report that as the
            // churn of `app/[slug]`. The name arrives from a path this server printed, so it is index
            // data one call further round. The scopes a caller genuinely writes as patterns — grep,
            // glob, list_tree — stay globs (ADR-0004); the ones built out of a path cannot.
            // A trailing '/' and the separator are appended rather than matched, so the scope selects
            // everything beneath the directory and never a sibling whose name merely starts with it.
            conditions.Add("starts_with(cf.path, $d)");
            parameters.Add(new DuckDBParameter("d", directoryInRepository.TrimEnd('/') + "/"));
        }

        // The terms every other filtered search takes, parsed by the same code rather than spelled a
        // second time (#117). Matched against the qualified path, because that is the path the caller
        // was shown and the one an exclude term is written against.
        if (PathTerms.Excluding(filters.Exclude, ChurnedPath(paths), "cx", parameters) is { } excluding)
            conditions.Add(excluding);

        // Bracketed, because it is a disjunction joining a list of conditions that are ANDed (#161):
        // without the parentheses the last extension would own the whole window and the scope.
        if (PathTerms.Including(filters.Extensions, ChurnedPath(paths), "ce", parameters) is { } including)
            conditions.Add($"({including})");

        return (conditions, parameters);
    }

    /// <summary>
    ///     The qualified path of a <c>commit_files</c> row, lower-cased for matching. A commit records
    ///     a path inside its repository, and the slug leads it only where the project puts it there
    ///     (ADR-0006) — so an exclude term reads against the path the caller was shown and not a
    ///     repository-relative one the project never prints.
    /// </summary>
    private static string ChurnedPath(ProjectPaths paths) =>
        paths.SingleRepository ? "lower(cf.path)" : "lower(c.repo_slug || '/' || cf.path)";

    /// <summary>
    ///     Whether a path a ranking returned is still there at HEAD, as a SQL fragment over a
    ///     <c>ranked</c> CTE with a <c>path</c> column. Written once because every ranking in this
    ///     system ranks paths a later commit deleted or renamed away, and a change to how "still there"
    ///     is recognised has to reach all of them or two answer differently about one path.
    ///     Always applied to what survived a LIMIT, never inside the aggregate that produced it, for
    ///     the reason <c>RankAsync</c> gives where it uses this.
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
                        {(includingBeneath
                            // starts_with and not GLOB, although a scope this project narrows by is
                            // always a glob (ADR-0004): the pattern there is written by a caller, and
                            // this one would be a directory name read out of the repository. A route
                            // directory called `[slug]` is a character class to GLOB, so the deleted
                            // one would match a sibling and be reported as still at HEAD — a silent
                            // wrong answer about the one thing this mark exists to say.
                            ? "OR starts_with(f.path, ranked.path || '/')"
                            : "")}))
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
    ///     The same again, narrowed to the commits that recorded a path at or beneath
    ///     <paramref name="pathInRepository" /> of <paramref name="pathRepositorySlug" /> — a semi-join
    ///     on <c>commit_files</c>, which is the join <c>file_history</c> already makes for one exact
    ///     path, widened here from a path to a prefix (#118).
    ///     Matched on the path a commit recorded, so the scope begins where a file was last renamed;
    ///     every reply that uses it says so, because a reorganised directory would otherwise read as a
    ///     quiet one. Taken literally rather than as a glob, for the reason
    ///     <see cref="AtHeadExists" /> gives: it is a name and `[slug]` is a character class (#122).
    /// </summary>
    public static (string Scope, List<DuckDBParameter> Parameters) CommitScope(string? repositorySlug, string? author,
        string? message = null, string? pathRepositorySlug = null, string? pathInRepository = null)
    {
        var clauses = new List<string>(3);
        var parameters = new List<DuckDBParameter>(3);
        if (pathRepositorySlug is not null)
        {
            // Only where it says something the repository clause below does not. A `repo` that names
            // the path's own repository is the common call, and `repo_slug = $r AND repo_slug = $pr`
            // reads as two scopes where there is one.
            if (!string.Equals(pathRepositorySlug, repositorySlug, StringComparison.Ordinal))
            {
                clauses.Add("repo_slug = $pr");
                parameters.Add(new DuckDBParameter("pr", pathRepositorySlug));
            }

            // An empty path is the repository's own root, which every commit of it is under: the
            // repository clause above is the whole scope and a semi-join matching everything is waste.
            if (!string.IsNullOrEmpty(pathInRepository))
            {
                clauses.Add("""
                            EXISTS (SELECT 1 FROM commit_files cf
                                    WHERE cf.commit_id = commits.commit_id
                                      AND (cf.path = $pp OR starts_with(cf.path, $pp || '/')))
                            """);
                parameters.Add(new DuckDBParameter("pp", pathInRepository.TrimEnd('/')));
            }
        }

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
