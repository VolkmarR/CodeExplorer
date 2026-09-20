using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     One path a commit touched. <see cref="Path" /> is the path inside its repository, as the commit
///     recorded it, and <see cref="QualifiedPath" /> is how the project names that path (ADR-0006) —
///     always set, with <see cref="AtHead" /> saying whether there is still a file at it, which is the
///     shape <see cref="ChurnedFile" /> already uses and for the same reason. A page can label a row
///     with the bare path because the commit above it says which repository that is; a tool reply has
///     no such column, and a bare <c>src/Old.cs</c> in a multi-repository project names a file an
///     agent cannot ask about.
/// </summary>
public sealed record CommitFile(string Path, string ChangeKind, int Added, int Deleted, string QualifiedPath,
    bool AtHead);

/// <summary>Everything one file's history asks for.</summary>
public sealed record FileHistoryRequest(string Path, int Limit);

/// <summary>
///     The commits that changed one file, newest first. Empty with <see cref="HasHistory" /> set is a
///     file whose history is older than what was imported or that reached its path by a rename; empty
///     without it is a project that has no history to look in.
///     <see cref="Path" /> and not the indexed file (#136): this answers for a path only the recorded
///     commit paths hold as readily as for a live one, and such a path has no row in <c>files</c> to
///     carry. <see cref="PathScope.AtHead" /> is which of the two it was, and the reply has to say it.
/// </summary>
public sealed record FileHistoryAnswer(
    PathScope Path,
    bool HasHistory,
    IReadOnlyList<RecordedChange> Commits) : Outcome;

/// <summary>
///     Everything a blame asks for. <see cref="EndLine" /> null — or anything below 1 — is the end of
///     the file, which the module bounds rather than the caller.
/// </summary>
public sealed record BlameRequest(string Path, int StartLine, int? EndLine);

/// <summary>
///     Who last changed each line of a window of a file. <see cref="First" /> and <see cref="Last" />
///     are the window that was read, after clamping, with <see cref="Last" /> null for the end of the
///     file, so a reply naming an empty answer's bounds names the ones it looked at. Empty runs is a
///     file the build kept without lines as much as it is a window past the end, which
///     <see cref="IndexedFile.SkipReason" /> tells apart.
/// </summary>
public sealed record BlameAnswer(
    IndexedFile File,
    bool HasHistory,
    int First,
    int? Last,
    IReadOnlyList<AttributedLines> Runs) : Outcome;

/// <summary>
///     Everything a churn ranking asks for. <see cref="Directory" /> is a qualified path, a bare
///     repository slug, or null for the whole project — one field, because a repository is a directory
///     and both callers hand over whichever of the two their surface spells.
///     <see cref="Depth" /> rolls the ranking up to the directories that many segments beneath
///     <see cref="Directory" />; null ranks files, which is what every surface but the tool asks for.
/// </summary>
/// <remarks>
///     <c>Exclude</c> is the comma-separated path terms every other filtered search here takes, and a
///     ranking needs them more than a search does: a machine-authored commit counts exactly like a
///     hand-written one, so regenerated output, a mechanical version bump or a bulk rename outranks
///     the code that drives it (#117).
/// </remarks>
public sealed record ChurnRequest(string? Directory, int Days, int Limit, int? Depth = null,
    string? Exclude = null);

/// <summary>
///     The most-changed files of a window, and everything needed to say what the ranking does not
///     cover. The four ways it can come back empty are kept apart rather than collapsed, because each
///     is a different sentence and an agent shown the wrong one learns a wrong fact (CONTEXT.md,
///     History): <see cref="HasHistory" /> false is a project nothing was imported for,
///     <see cref="Window" /> null is a scope with no commit at all, an empty <see cref="Files" /> is a
///     window in which nothing changed, and <see cref="Coverage" /> names the repositories the ranking
///     could not speak for whichever of those it is.
///     <see cref="ScopeSpelled" /> is what the answer calls the scope, so a ranking and a miss name it
///     the same way.
///     <see cref="Depth" /> is the depth the rows were rolled up to, or null where they are files. It
///     is carried rather than inferred from the request, because what a reply calls its rows has to be
///     what the query grouped them by.
///     <see cref="Hidden" /> is how many paths the <c>exclude</c> terms kept out, zero where there were
///     none. A filtered ranking reads exactly like an unfiltered one, so the number is carried and said
///     rather than left for the caller to remember it asked.
/// </summary>
public sealed record ChurnAnswer(
    string ScopeSpelled,
    bool HasHistory,
    HistoryWindow? Window,
    IReadOnlyList<ChurnedFile> Files,
    HistoryCoverage Coverage,
    // Not defaulted: an answer always knows its own grain, and a default is a later construction
    // quietly calling a ranking of directories a ranking of files.
    int? Depth,
    int Hidden = 0,
    // What the scope was called before (#131), null where it was called nothing else or where no
    // directory was named. The ranking's own counts are untouched by it: renames are signalled here,
    // never followed.
    PathLineage? Lineage = null) : Outcome;

/// <summary>Everything the paths of one commit ask for. The SHA is full: a listing is where it came from.</summary>
public sealed record CommitFilesRequest(string Sha);

/// <summary>Everything one commit's own record asks for. The SHA is full, like the file list's.</summary>
public sealed record CommitRequest(string Sha);

/// <summary>
///     One commit as the change log would list it, read on its own because something linked to it. A
///     SHA the index does not hold is a miss and never an answer with empty fields: a page drawn from
///     one would claim a commit that changed nothing and was written by nobody.
/// </summary>
public sealed record CommitAnswer(LoggedCommit Commit) : Outcome;

/// <summary>The paths one commit touched, in path order.</summary>
public sealed record CommitFilesAnswer(string Sha, IReadOnlyList<CommitFile> Files) : Outcome;

/// <summary>
///     The history reads that answer about one file or one commit — <c>file_history</c>,
///     <c>blame</c>, <c>commit</c> and <c>commit_files</c> — and the churn ranking, which is the same
///     rows asked how often rather than when. The abbreviated SHA an agent quotes is resolved here,
///     because the two commit reads are the only callers that take one.
/// </summary>
public sealed partial class HistoryQueries
{
    /// <summary>
    ///     The commits that touched one path, newest first. The path is resolved before the history is
    ///     asked about, so an agent that got the path wrong is told so rather than told about history.
    ///     A path only the recorded commit paths hold is listed too, the same way a live one is (#136):
    ///     this is the log-shaped read of one path, and refusing the very path git_log had just answered
    ///     for told an agent it had made a typo it had not made.
    /// </summary>
    public Task<Outcome> FileHistoryAsync(string slug, FileHistoryRequest request,
        CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => readers.OverIndexAsync(slug, null, async (index, token) =>
        {
            var (file, recorded, unresolved) = await OnePathAsync(index, request.Path, token);
            if (unresolved is not null) return unresolved;
            var scope = recorded
                        ?? new PathScope(file!.QualifiedPath, file.RepositorySlug, file.PathInRepository);
            scope = scope with
            {
                Lineage = await LineageAsync(index, scope.RepositorySlug, scope.PathInRepository,
                    await SpellerAsync(index, scope.RepositorySlug, token), token)
            };

            bool hasHistory = await HasHistoryAsync(index, token);
            var commits = hasHistory
                ? await PathCommitsAsync(index, scope.RepositorySlug, scope.PathInRepository,
                    Math.Clamp(request.Limit, 1, MaxCommits), token)
                : [];
            return new FileHistoryAnswer(scope, hasHistory, commits);
        }, cancellationToken), (FileHistoryAnswer answer) => new Telemetry.Measured(answer.Commits.Count, 0));

    /// <summary>
    ///     Who last changed each line of a window of one file, as runs. A file the build kept without
    ///     lines answers with no runs rather than a refusal: it is in the index, and only the
    ///     attribution has nothing to fill.
    ///     A path only history records is refused, and says why (#136). That is not a gap: attribution
    ///     is keyed to the newest recorded commit, so there is nothing here to attribute.
    /// </summary>
    public Task<Outcome> BlameAsync(string slug, BlameRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => readers.OverIndexAsync(slug, null,
            async (index, token) =>
            {
                var (file, recorded, unresolved) = await OnePathAsync(index, request.Path, token);
                if (unresolved is not null) return unresolved;
                if (recorded is { } gone) return GoneFromHead(gone.Spelled, "blame", BlameCannot);

                bool hasHistory = await HasHistoryAsync(index, token);
                int first = Math.Max(1, request.StartLine);
                // Anything below 1 is the end of the file, which is the caller saying "all of it" in the
                // two spellings its surface allows: an omitted argument and a zero.
                int? last = request.EndLine is null or < 1 ? null : request.EndLine;
                IReadOnlyList<AttributedLines> runs = file!.SkipReason is null
                    ? await RunsAsync(index, file.FileId, first, Math.Min(last ?? MaxLinesPerFile, MaxLinesPerFile),
                        token)
                    : [];
                return new BlameAnswer(file, hasHistory, first, last, runs);
            }, cancellationToken), (BlameAnswer answer) => new Telemetry.Measured(answer.Runs.Count, 0));

    /// <summary>
    ///     The files a window's commits touched, most commits first, within a scope the caller names
    ///     with a qualified path.
    ///     The order of the decisions is the answer's meaning: whether the project has history at all,
    ///     then what the scope is, then whether that scope holds a commit, then which repositories the
    ///     ranking cannot speak for, and only then the ranking. Each step's answer is carried rather
    ///     than turned into an empty ranking, because an empty ranking is a claim — that nothing changed
    ///     — and three of the four steps above would be making it falsely.
    /// </summary>
    public Task<Outcome> ChurnAsync(string slug, ChurnRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => readers.OverDirectoryAsync(slug, request.Directory,
            async (index, directory, token) =>
            {
                bool hasHistory = await HasHistoryAsync(index, token);
                var (repositorySlug, directoryInRepository, spelled) = Scope(index, directory);

                var window = hasHistory
                    ? await IndexQueries.WindowAsync(index.Connection, request.Days, repositorySlug, token)
                    : null;
                // Read whether or not there is a ranking, and before the answer branches: a reader shown
                // nothing is the one most likely to conclude that nothing changed.
                var coverage = await index.HistoryCoverageAsync(repositorySlug, token);
                int? depth = request.Depth is { } requested ? Math.Clamp(requested, 1, MaxRollupDepth) : null;
                int limit = Math.Clamp(request.Limit, 1, MaxRankedFiles);
                var paths = await index.PathsAsync(token);
                IReadOnlyList<ChurnedFile> ranked = window is null
                    ? []
                    : depth is { } rollup
                        ? await IndexQueries.RankDirectoriesAsync(index.Connection, paths, window, repositorySlug,
                            directoryInRepository, rollup, request.Exclude, limit, token)
                        : await IndexQueries.RankAsync(index.Connection, paths, window, repositorySlug,
                            directoryInRepository, request.Exclude, limit, token);
                int hidden = window is null
                    ? 0
                    : await IndexQueries.HiddenByExcludeAsync(index.Connection, paths, window, repositorySlug,
                        directoryInRepository, request.Exclude, token);

                // A ranking of a scope that does not exist is the emptiest kind of empty answer, and a
                // path prefixed with the project's slug is the commonest way to ask for one (#111). The
                // diagnosis is the reader's, so hot_files says what read_file and list_tree say. Asked
                // of both grains: a rollup of a scope that is not there is empty for the same reason.
                if (ranked.Count == 0 && request.Directory is { } wanted
                                      && await index.DirectorySlugPrefixAdviceAsync(wanted, token) is { } slugged)
                    return new Problem(slugged);

                // Only where a directory was named: the whole project and a repository root have no
                // previous path to have, and an unscoped ranking must not pay for asking.
                var lineage = repositorySlug is null || directoryInRepository is null
                    ? null
                    : await LineageAsync(index, repositorySlug, directoryInRepository,
                        await SpellerAsync(index, repositorySlug, token), token);
                return new ChurnAnswer(spelled, hasHistory, window, ranked, coverage, depth, hidden, lineage);
            }, cancellationToken), (ChurnAnswer answer) => new Telemetry.Measured(answer.Files.Count, 0));

    /// <summary>
    ///     One commit, by SHA. Its own read rather than a page of the log filtered down, because the
    ///     caller here arrived by a link and knows only the SHA — finding it in the log would mean
    ///     paging until it turned up, and a commit old enough is past the ceiling every page runs under.
    ///     Separate from its file list for the reason blame is separate from the file: the page draws
    ///     the message and the sums as soon as it has them, and a commit touching hundreds of paths
    ///     should not hold that back.
    /// </summary>
    public Task<Outcome> CommitAsync(string slug, CommitRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => readers.OverIndexAsync(slug, null,
            async (index, token) => await OverShaAsync(index, slug, request.Sha, async (sha, inner) =>
            {
                // Read back rather than carried over: the resolution and this read are two statements,
                // and a swap between them leaves a SHA that was in the index and is not any more.
                var commit = await OneLoggedAsync(index, sha, inner);
                return commit is null
                    ? new Problem(NoSuchCommit(request.Sha, slug), ProblemKind.Missing)
                    : new CommitAnswer(commit);
            }, token), cancellationToken), (CommitAnswer _) => new Telemetry.Measured(1, 0));

    /// <summary>
    ///     The paths one commit touched. A SHA the index does not hold is a miss and not an empty
    ///     answer: every commit the walk records touched something, the root included, so nothing to
    ///     list means nothing to list it for. Resolving the SHA first is what lets the two be told
    ///     apart — a commit that really did touch nothing, an empty one or a merge that changed nothing
    ///     against its first parent, is a miss that says which of the two it is.
    /// </summary>
    public Task<Outcome> CommitFilesAsync(string slug, CommitFilesRequest request,
        CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => readers.OverIndexAsync(slug, null,
            async (index, token) => await OverShaAsync(index, slug, request.Sha, async (sha, inner) =>
            {
                var files = await PathsOfAsync(index, sha, inner);
                return files.Count == 0
                    // Kept a miss, and so a 404 on the route the operator's page reads: a page drawn
                    // from an empty list would say "this commit changed nothing" in its own layout,
                    // where this says it in words and says what that means.
                    ? new Problem(
                        $"Commit {sha} of project '{slug}' touched no path. It is an empty commit, or a merge "
                        + "that changed nothing against its first parent; history records what a commit did to "
                        + "its first parent, and nothing came in by that route.", ProblemKind.Missing)
                    : new CommitFilesAnswer(sha, files);
            }, token), cancellationToken), (CommitFilesAnswer answer) => new Telemetry.Measured(answer.Files.Count, 0));

    /// <summary>
    ///     How many commits a prefix may fit before the refusal stops naming them all. Four, because
    ///     the list is there to be compared against what the caller has and not to be complete: a
    ///     prefix that fits more than four is one the caller has to lengthen whatever the rest are.
    /// </summary>
    private const int MaxNamedShas = 4;

    /// <summary>
    ///     What git itself requires of an abbreviated SHA, and for the same reason: below four
    ///     characters a prefix that happens to fit one commit today fits two after the next refresh,
    ///     and the answer it gave was never about the commit the caller meant.
    /// </summary>
    private const int MinShaPrefix = 4;

    /// <summary>
    ///     Runs an answer against the one commit a caller meant, from as much of the SHA as they had.
    ///     A prefix and not the whole SHA, because every reply an agent reads abbreviates to eight
    ///     characters — git_log, file_history and blame all do — so the full forty is a thing no tool
    ///     surface ever hands out, and a read that insisted on it could not be reached from any of
    ///     them. The API and the operator's pages send whole SHAs and are unaffected in what they get
    ///     back, a whole SHA being a prefix of itself; what they gain is this method's two refusals,
    ///     for a SHA too short to be one and for one that fits several.
    ///     More than one match is refused rather than resolved to the first. Two commits sharing eight
    ///     characters is rare and a confident answer about the wrong one is undetectable, which is the
    ///     trade every git client makes the same way.
    ///     Shaped as a continuation rather than as a SHA-or-problem pair for the reason
    ///     <see cref="IndexReaders.OverFileAsync" /> is: the caller that gets a SHA is the only one that
    ///     runs, so there is no second state for it to have to rule out first.
    /// </summary>
    private static async Task<Outcome> OverShaAsync(IndexReader index, string slug, string given,
        Func<string, CancellationToken, Task<Outcome>> answer, CancellationToken cancellationToken)
    {
        // Lower-cased because git writes SHAs in hex lower case and a pasted one may not be, and
        // trimmed because a SHA copied out of a reply brings its spacing with it.
        string prefix = given.Trim().ToLowerInvariant();
        if (prefix.Length < MinShaPrefix)
            return new Problem(
                (prefix.Length == 0
                    ? $"No commit SHA was given, so no commit of project '{slug}' can be named. "
                    : $"'{given}' is too short to name a commit of project '{slug}': give at least {MinShaPrefix} characters of the SHA. ")
                + "git_log, file_history and blame each print the first eight, which is enough.",
                ProblemKind.Invalid);

        var matches = await ShasAsync(index, prefix, MaxNamedShas, cancellationToken);
        if (matches.Count == 0) return new Problem(NoSuchCommit(given, slug), ProblemKind.Missing);
        if (matches.Count == 1) return await answer(matches[0], cancellationToken);

        // The candidates rather than a count: a caller holding eight characters of one of them can see
        // which it meant and lengthen its argument, where a count only says to try again.
        return new Problem(
            $"'{given}' starts the SHA of more than one commit of project '{slug}': "
            + $"{string.Join(", ", matches.Select(sha => sha[..12]))}"
            + (matches.Count == MaxNamedShas ? ", and possibly others" : "")
            + ". Give more of the SHA.", ProblemKind.Invalid);
    }

    /// <summary>
    ///     The one sentence a SHA that names no commit is answered with, wherever it was asked. Written
    ///     once because the record and the file list are two halves of one workflow: a caller that
    ///     tries the second after the first has to read one fact, not two shapes of it.
    /// </summary>
    private static string NoSuchCommit(string sha, string slug) =>
        $"No commit '{sha}' in the history of project '{slug}'.";

    /// <summary>
    ///     The commits whose SHA starts with what the caller gave, at most <paramref name="ceiling" />
    ///     of them. Distinct, because two repositories of one project can hold the same commit and that
    ///     is one commit to a caller rather than an ambiguous prefix.
    ///     <c>starts_with</c> and not <c>LIKE</c>: the prefix is caller text, and under LIKE a stray
    ///     <c>%</c> in it would widen the match instead of failing to find one.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ShasAsync(IndexReader index, string prefix, int ceiling,
        CancellationToken cancellationToken)
    {
        using var command = index.Connection.Query($"""
                                                    SELECT DISTINCT sha FROM commits
                                                    WHERE starts_with(sha, $p)
                                                    ORDER BY sha
                                                    LIMIT {ceiling}
                                                    """, [new DuckDBParameter("p", prefix)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var shas = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) shas.Add(reader.Text("sha"));
        return shas;
    }

    /// <summary>
    ///     Who last changed each line of a file, as runs rather than as one row per line: consecutive
    ///     lines sharing a commit are one entry, which is how blame reads and a fraction of the output.
    /// </summary>
    private static async Task<IReadOnlyList<AttributedLines>> RunsAsync(IndexReader index, long fileId, int first,
        int last, CancellationToken cancellationToken)
    {
        // The runs are rebuilt from lines rather than read from attribution, because attribution is
        // keyed by blob and holds the ranges of the whole file: a window of it would have to be clipped
        // here anyway, and a line the build could not attribute is absent there but present here.
        // Gaps and islands: within one commit, consecutive line numbers have a constant difference from
        // their position in that commit's lines, so that difference is the run. The window function is
        // computed in a subquery because it is evaluated after grouping and cannot appear in GROUP BY.
        // Lines with no commit fall in one NULL partition, which islands correctly for the same reason.
        using var command = index.Connection.Query("""
                                                   SELECT min(line_number) AS start_line,
                                                          max(line_number) AS end_line,
                                                          sha, author_name, authored_at, subject
                                                   FROM (SELECT l.line_number, c.sha, c.author_name, c.authored_at,
                                                                c.subject,
                                                                l.line_number - row_number() OVER (
                                                                    PARTITION BY c.sha ORDER BY l.line_number) AS run
                                                         FROM lines l LEFT JOIN commits c USING (commit_id)
                                                         WHERE l.file_id = $f AND l.line_number BETWEEN $a AND $b)
                                                   GROUP BY sha, author_name, authored_at, subject, run
                                                   ORDER BY start_line
                                                   """,
            [new DuckDBParameter("f", fileId), new DuckDBParameter("a", first), new DuckDBParameter("b", last)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var runs = new List<AttributedLines>();
        while (await reader.ReadAsync(cancellationToken))
            runs.Add(new AttributedLines(reader.Int32("start_line"), reader.Int32("end_line"),
                reader.Attribution()));
        return runs;
    }

    /// <summary>
    ///     The paths one commit touched, each named the way the project names it and marked with
    ///     whether HEAD still holds a file there. Matched by whole SHA, which is what
    ///     <see cref="OverShaAsync" /> has already resolved whatever the caller gave.
    ///     The naming is built from the commit's own repository rather than read from <c>files</c>,
    ///     because the paths that need it most are exactly the ones with no row there.
    ///     One <c>commit_id</c> and not every row sharing the SHA: two repositories of a project can
    ///     hold the same commit, and listing both would name every path twice and count them twice. The
    ///     lowest id is the one <see cref="OneLoggedAsync" /> reports, so the record and the paths are
    ///     about the same copy of it.
    /// </summary>
    private static async Task<IReadOnlyList<CommitFile>> PathsOfAsync(IndexReader index, string sha,
        CancellationToken cancellationToken)
    {
        var paths = await index.PathsAsync(cancellationToken);
        using var command = index.Connection.Query("""
                                                   SELECT cf.path, cf.change_kind, cf.added, cf.deleted,
                                                          c.repo_slug, f.qualified_path
                                                   FROM commits c JOIN commit_files cf USING (commit_id)
                                                   LEFT JOIN repositories r ON r.slug = c.repo_slug
                                                   LEFT JOIN files f ON f.repo_id = r.repo_id AND f.path = cf.path
                                                   WHERE c.commit_id = (SELECT min(commit_id) FROM commits
                                                                        WHERE sha = $sha)
                                                   ORDER BY cf.path
                                                   """, [new DuckDBParameter("sha", sha)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var files = new List<CommitFile>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new CommitFile(reader.Text("path"), reader.Text("change_kind"), reader.Int32("added"),
                reader.Int32("deleted"), paths.Format(reader.Text("repo_slug"), reader.Text("path")),
                !reader.IsNull("qualified_path")));
        return files;
    }

}
