using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     One file that kept changing alongside another: how many of the anchor's commits also touched
///     it. <see cref="QualifiedPath" /> is how the project names it (ADR-0006) and is set even for a
///     path HEAD no longer holds, which <see cref="AtHead" /> is what says — coupling that happened
///     is still coupling, and an agent sent to read a file that is gone has been told something false.
/// </summary>
public sealed record CoChangedFile(string QualifiedPath, bool AtHead, int SharedCommits);

/// <summary>
///     What one file's coupling amounts to over a window, and what the answer had to leave out to say
///     it. <see cref="Paired" /> is the commits the ranking is drawn from and <see cref="Commits" />
///     every commit that touched the file, so the difference is the mass commits the ceiling excluded
///     — a number the reply says out loud, because a ranking drawn from a third of a file's history
///     without saying so is the one that misleads.
/// </summary>
/// <param name="Commits">Commits of the window that touched the anchor file, ceiling or no ceiling.</param>
/// <param name="Paired">How many of those were small enough to pair.</param>
/// <param name="Files">The co-changed files, most shared commits first.</param>
public sealed record CoChanges(int Commits, int Paired, IReadOnlyList<CoChangedFile> Files)
{
    /// <summary>The commits the ceiling kept out of the pairing.</summary>
    public int Excluded => Commits - Paired;
}

/// <summary>Everything a co-change ranking asks for.</summary>
public sealed record CoChangeRequest(string Path, int Days, int Limit);

/// <summary>
///     What the index holds for a path outside any window: how many commits recorded it at all, and
///     when the newest of them was. Read only where a window came back empty, because that is the one
///     place the two ways of being empty have opposite meanings — a path git has never recorded under
///     this spelling, usually because a rename severed its history, against one whose commits are
///     simply older than the days asked for.
/// </summary>
/// <param name="Commits">Commits recorded for the path, over the whole imported history.</param>
/// <param name="Newest">When the newest of them was authored, or null where there are none.</param>
public sealed record RecordedPath(int Commits, DateTimeOffset? Newest);

/// <summary>
///     What one file changed alongside over a window. <see cref="MaxCommitPaths" /> is the ceiling the
///     pairing ran under, carried so that the reply explaining what was excluded quotes the number that
///     was actually used rather than reading the configuration a second time.
///     <see cref="Recorded" /> is what the path has outside the window, filled only where the window
///     reached none of its commits — the branch where an empty answer has to say which kind of empty
///     it is, and the only one that pays for the extra read.
/// </summary>
public sealed record CoChangeAnswer(
    IndexedFile File,
    bool HasHistory,
    HistoryWindow? Window,
    CoChanges Coupling,
    int MaxCommitPaths,
    RecordedPath? Recorded = null,
    PathLineage? Lineage = null) : Outcome;

/// <summary>
///     Co-Change (CONTEXT.md): which files keep moving with one file. Alone here because it is the
///     one read that pairs commits rather than listing them — the ceiling on how wide a commit may be
///     and the walk over an anchor's earlier paths are decisions no other answer takes.
/// </summary>
public sealed partial class HistoryQueries
{
    /// <summary>
    ///     The files a window's commits changed alongside one file, most shared commits first. Scoped to
    ///     the anchor's own repository, because that is the only one whose commits could have carried it
    ///     — and so the window is that repository's newest commit, not another's.
    ///     A path only history records is refused, in the same words and with the same redirect blame
    ///     gives it (#136). The pairing could read it, and deliberately does not: an agent meeting one
    ///     story from both tools is worth more than a fourth state settled by symmetry.
    /// </summary>
    public Task<Outcome> CoChangedAsync(string slug, CoChangeRequest request,
        CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => readers.OverIndexAsync(slug, null,
            async (index, token) =>
            {
                var (found, historical, unresolved) = await OnePathAsync(index, request.Path, token);
                if (unresolved is not null) return unresolved;
                if (historical is { } gone) return GoneFromHead(gone.Spelled, "co_changed", CoChangedCannot);
                var file = found!;

                bool hasHistory = await HasHistoryAsync(index, token);
                var window = hasHistory
                    ? await IndexQueries.WindowAsync(index.Connection, request.Days, file.RepositorySlug, token)
                    : null;
                // Resolved before the pairing rather than after it: the pairing spans the chain now, so
                // it is an input and no longer only something the reply says (#143).
                var lineage = await LineageAsync(index, file.RepositorySlug, file.PathInRepository, token);
                // The untruncated chain, so the ranking covers exactly what the combined total counted.
                IReadOnlyList<string> anchored = [file.PathInRepository, ..lineage?.Chain ?? []];
                var coupling = window is null
                    ? new CoChanges(0, 0, [])
                    : await PairAsync(index, window, file.RepositorySlug, anchored, _maxCommitPaths,
                        Math.Clamp(request.Limit, 1, MaxRankedFiles), token);
                // One extra read, and only on the branch that cannot answer without it: a window that
                // reached nothing has to say whether the path has any history at all.
                // Over the same chain the pairing ran on, not the current path alone: every other number
                // in this answer is chain-wide now, and a reason-for-nothing quoting the post-rename
                // slice would explain a chain-wide emptiness with a path-wide count (#143).
                var recorded = window is not null && coupling.Commits == 0
                    ? await RecordedPathAsync(index, file.RepositorySlug, anchored, token)
                    : null;
                return new CoChangeAnswer(file, hasHistory, window, coupling, _maxCommitPaths, recorded, lineage);
            }, cancellationToken), (CoChangeAnswer answer) => new Telemetry.Measured(answer.Coupling.Files.Count, 0));

    /// <param name="index">The index being read.</param>
    /// <param name="repositorySlug">The repository the paths belong to.</param>
    /// <param name="paths">
    ///     The anchor's path, and every earlier one a rename leads back to. A list and not one path
    ///     because the pairing this explains the emptiness of spans the same list (#143), and the two
    ///     have to be counted over the same thing or the explanation contradicts the answer.
    /// </param>
    /// <param name="cancellationToken">Threaded to the command.</param>
    private static async Task<RecordedPath> RecordedPathAsync(IndexReader index, string repositorySlug,
        IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter> { new("r", repositorySlug) };
        var names = BindPaths(parameters, paths);

        using var command = index.Connection.Query($"""
                                                    SELECT count(DISTINCT cf.commit_id) AS commits,
                                                           max(c.authored_at) AS newest
                                                    FROM commit_files cf JOIN commits c USING (commit_id)
                                                    WHERE c.repo_slug = $r AND cf.path IN ({string.Join(", ", names)})
                                                    """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new RecordedPath(0, null);
        int commits = (int)reader.Int64("commits");
        return new RecordedPath(commits,
            commits == 0 || reader.IsNull("newest")
                ? null
                : reader.Timestamp("newest"));
    }

    /// <summary>
    ///     The files a window's commits changed alongside one file, most shared commits first. The
    ///     coupling the code itself does not show — a constant and the three places that read it, two
    ///     files that have simply always moved together.
    ///     Pairing is anchored rather than a self-join of <c>commit_files</c> against itself. A
    ///     self-join is the obvious reading of "which files change together" and it is quadratic in
    ///     every commit of the window at once: one vendor drop of five thousand paths is twenty-five
    ///     million pairs on its own, computed to answer a question about one file. Anchored, the
    ///     pairing is the anchor's own commits times what each of them touched, and reading them costs
    ///     two passes over <c>commit_files</c> rather than a pass per commit in the window.
    ///     The ceiling is still needed for what it was needed for: one mass commit that happens to
    ///     touch the anchor pairs it with every path in the repository at once, and those pairs are not
    ///     coupling, they are one commit. Excluding them is the reply's to explain, which is why the
    ///     count of what was excluded comes back rather than being quietly dropped.
    ///     Here rather than in <see cref="IndexQueries" />: that class is for the reads a build makes
    ///     too, and no build pairs anything.
    /// </summary>
    /// <remarks>
    ///     <paramref name="anchorPaths" /> is the anchor's own path and every earlier one a rename leads
    ///     back to, so coupling survives a move (#143). It is the one place in this system where a
    ///     rename is followed rather than signalled: every other path-scoped read counts what its own
    ///     path recorded (CONTEXT.md, Previous Path), and the exception is here because <c>git_log</c>
    ///     and <c>file_history</c> can answer for a previous path while nothing can answer its coupling.
    ///     In the commit that moved the anchor, a counterpart that <b>also moved</b> is dropped and one
    ///     that was edited is kept. Dropping the whole commit was the first shape and it was too blunt:
    ///     a commit that renames a file and updates its two callers is three paths, nowhere near the
    ///     ceiling, and is the strongest coupling evidence the anchor has. What is not evidence is the
    ///     other half of the same commit — "these moved together" is one commit and not a relationship,
    ///     which is the paths ceiling's reasoning applied per path instead of per commit. Splitting it
    ///     this way needs no second threshold, and leaves the commit counted and paired as it is.
    /// </remarks>
    private static async Task<CoChanges> PairAsync(IndexReader index, HistoryWindow window, string repositorySlug,
        IReadOnlyList<string> anchorPaths, int maxCommitPaths, int limit, CancellationToken cancellationToken)
    {
        // Epoch seconds rather than timestamp parameters, for the reason IndexQueries compares them
        // that way: it keeps the comparison off the session time zone and a DateTimeOffset out of the
        // driver's parameter mapping.
        var parameters = new List<DuckDBParameter>
        {
            new("since", window.Since.ToUnixTimeSeconds()),
            new("until", window.Until.ToUnixTimeSeconds()),
            new("r", repositorySlug),
            new("c", maxCommitPaths)
        };
        string anchored = string.Join(", ", BindPaths(parameters, anchorPaths));

        using var command = index.Connection.Query($"""
                                                    -- The anchor's own commits first, and everything after
                                                    -- reads only those. Narrowing here rather than later is
                                                    -- what keeps the work proportional to one file's history
                                                    -- instead of to the whole window's.
                                                    WITH anchor AS (
                                                        SELECT cf.commit_id,
                                                               -- Whether this commit is the one that moved
                                                               -- the anchor. A rename is one row, carrying
                                                               -- the new path, so only the commit that did
                                                               -- the moving matches.
                                                               bool_or(cf.change_kind = 'renamed') AS moved
                                                        FROM commit_files cf
                                                        JOIN commits c USING (commit_id)
                                                        WHERE c.repo_slug = $r
                                                          AND epoch(c.authored_at) BETWEEN $since AND $until
                                                          AND cf.path IN ({anchored})
                                                        GROUP BY cf.commit_id),
                                                    -- Every path those commits touched. The window and the
                                                    -- repository are not repeated: a commit_id from anchor
                                                    -- already satisfies both. MATERIALIZED because two CTEs
                                                    -- below read this one, and inlined it would be a second
                                                    -- scan of commit_files to produce the same rows.
                                                    -- `moved` is carried through rather than joined again
                                                    -- below: anchor groups over commit_files, and reading
                                                    -- it twice would run that scan twice for one answer.
                                                    touched AS MATERIALIZED (
                                                        SELECT cf.commit_id, cf.path, cf.change_kind,
                                                               anchor.moved
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
                                                        -- The whole chain and not just the current path:
                                                        -- an anchor's own earlier name is not a file it
                                                        -- co-changed with.
                                                        WHERE s.paired AND t.path NOT IN ({anchored})
                                                          -- In the commit that moved the anchor, a path
                                                          -- that moved with it says only "these moved
                                                          -- together"; one that was edited in the same
                                                          -- commit is real coupling and stays.
                                                          AND NOT (t.moved AND t.change_kind = 'renamed')
                                                        GROUP BY t.path
                                                        -- Spelled out rather than ordered by the alias, for
                                                        -- the reason IndexQueries spells its ORDER BY out.
                                                        ORDER BY count(*) DESC, t.path
                                                        -- Inlined and not parameterised: it is an int the
                                                        -- module has already clamped to a range, so there is
                                                        -- nothing to escape, and the churn ranking inlines
                                                        -- its own the same way.
                                                        LIMIT {limit})
                                                    -- LEFT JOIN ON TRUE so the counts survive an empty
                                                    -- ranking: a file that moves alone still has to say how
                                                    -- many commits it was looked at over. The cross join
                                                    -- does not carry ranked's order, so the outer ORDER BY
                                                    -- is what makes the ranking a ranking.
                                                    SELECT counts.commits, counts.paired, ranked.path,
                                                           ranked.shared,
                                                           {IndexQueries.AtHeadExists("$r")} AS at_head
                                                    FROM counts LEFT JOIN ranked ON TRUE
                                                    ORDER BY ranked.shared DESC, ranked.path
                                                    """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var paths = await index.PathsAsync(cancellationToken);
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

}
