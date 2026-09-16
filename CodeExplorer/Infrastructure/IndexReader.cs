using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     A row of <c>files</c>. <see cref="SkipReason" /> is set when the file is committed but has no lines in the
///     index.
/// </summary>
public sealed record IndexedFile(
    long FileId,
    string QualifiedPath,
    string RepositorySlug,
    int LineCount,
    long SizeBytes,
    string? SkipReason);

/// <summary>A row of <c>repositories</c>: what the last build read and where it stood.</summary>
/// <summary>
///     One repository as the last build left it. <paramref name="Commits" /> is how much history was
///     imported for it and <paramref name="NewestCommit" /> the last one recorded; zero and null mean
///     none was, which is a different thing from a repository nobody has changed.
/// </summary>
public sealed record IndexedRepository(
    string Slug,
    string Url,
    string HeadCommit,
    int FileCount,
    long LineCount,
    long Commits = 0,
    AttributedBy? NewestCommit = null);

/// <summary>
///     What an index holds, for the readers that describe a project rather than read from it:
///     <c>repo_info</c> and the operator's pages. <see cref="Repositories" /> is what the last build
///     read; <see cref="Files" /> and <see cref="Lines" /> are the sums the build recorded on them,
///     not a second count over <c>files</c>, so the two cannot disagree. <see cref="SingleRepository" />
///     is how the build named its files (ADR-0006), read from the index rather than the control
///     database because an index names things the way the build that wrote it was told to.
/// </summary>
public sealed record IndexStatus(
    DateTimeOffset BuiltAt,
    bool FtsIndexed,
    bool SingleRepository,
    IReadOnlyList<IndexedRepository> Repositories)
{
    public int Files => Repositories.Sum(r => r.FileCount);
    public long Lines => Repositories.Sum(r => r.LineCount);
}

/// <summary><see cref="Skipped" /> of the <see cref="Files" /> have no lines; <see cref="Lines" /> covers the rest.</summary>
public sealed record ExtensionCount(string Extension, int Files, long Lines, int Skipped);

/// <summary>One commit as a tool reports it: enough to name it and to say who and when, and no body.</summary>
public sealed record RecordedChange(
    string Sha,
    string RepositorySlug,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject);

/// <summary>The commit a run of lines is attributed to, or null for lines the build could not attribute.</summary>
public sealed record AttributedBy(string Sha, string AuthorName, DateTimeOffset AuthoredAt, string Subject);

/// <summary>
///     One commit as the change log lists it: a <see cref="RecordedChange" /> with its message body and
///     what it did to the tree, summed from <c>commit_files</c>. The sums are the commit's own added and
///     removed lines, so a reformat and a one-line fix read differently at a glance.
/// </summary>
public sealed record LoggedCommit(
    string Sha,
    string RepositorySlug,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject,
    string Body,
    int FilesChanged,
    int Added,
    int Deleted);

/// <summary>
///     One path a commit touched. <see cref="QualifiedPath" /> is set when the path is still at HEAD,
///     so a view can link to the file; null for a path the commit deleted or a later one renamed.
/// </summary>
public sealed record CommitFile(string Path, string ChangeKind, int Added, int Deleted, string? QualifiedPath);

/// <summary>A run of consecutive lines sharing one attribution (CONTEXT.md, Attribution).</summary>
public sealed record AttributedLines(int StartLine, int EndLine, AttributedBy? By);

/// <summary>
///     What a file's own history amounts to: the commit it was first changed by within the imported
///     history, and the one it was last changed by. Both null where none was imported.
/// </summary>
public sealed record FileCommits(AttributedBy? First, AttributedBy? Last);

/// <summary>
///     One row of a directory listing. A directory carries what lies beneath it — <see cref="Files" />
///     counts every file at any depth, not just its immediate children — and a file carries its own
///     size, with <see cref="Files" /> null to tell the two apart. <see cref="QualifiedPath" /> is what
///     the next listing is asked for, or what the file view opens.
/// </summary>
public sealed record TreeItem(
    string Name,
    string QualifiedPath,
    int? Files,
    long Lines,
    long SizeBytes,
    string? SkipReason);

/// <summary>
///     <see cref="Total" /> counts every match, <see cref="Files" /> the first <c>limit</c> of them.
///     <see cref="MatchesInOtherRepositories" /> is filled only when a repository-scoped glob matched
///     nothing, so a scoped miss is told apart from a pattern that matches nowhere.
/// </summary>
public sealed record GlobResult(int Total, IReadOnlyList<IndexedFile> Files, int? MatchesInOtherRepositories);

/// <summary>
///     What <see cref="IndexReader.OpenAsync" /> answers. Either the project's index is open for one
///     call, or it is not and the case says why in agent-facing prose: a semantic failure is an answer,
///     never an exception (CODING_STANDARDS). Every reader — MCP tool, operator endpoint, search — gets
///     the same three cases, so a project with nothing to read from or a <c>repo</c> that names nothing
///     is explained in one wording everywhere rather than eleven.
/// </summary>
public abstract record IndexOpen
{
    /// <summary>The caller disposes the reader after one unit of work and never keeps it (ADR-0003).</summary>
    public sealed record Opened(IndexReader Reader) : IndexOpen;

    /// <summary>The two ways an open is declined, with the prose to hand the caller.</summary>
    public abstract record Refused(string Explanation) : IndexOpen;

    /// <summary>Never built, or a refresh is still building the first one.</summary>
    public sealed record NoIndex(string Explanation) : Refused(Explanation);

    /// <summary>The <c>repo</c> argument names no repository in this index.</summary>
    public sealed record UnknownRepository(string Explanation) : Refused(Explanation);
}

/// <summary>
///     The one way a project's index is read. <see cref="OpenAsync" /> is the seam every reader
///     crosses — the MCP tools, the operator UI's endpoints and the three text searches — and what it
///     hands back is a connection bound to the project for one call (ADR-0003) together with the
///     answers every reader used to derive for itself: which repositories the build read, how this
///     project spells a path (ADR-0006), and whether a <c>repo</c> argument names one of them. The
///     read-only questions that are not text searches — what exists, what a file says, how big things
///     are — are asked of the reader too. Glob matching is the SQL <c>GLOB</c> operator (ADR-0004), so
///     <c>*</c> crosses <c>/</c> and there is no brace expansion.
///     Callers dispose it and never keep it: releasing the lease is what lets a swap proceed.
/// </summary>
public sealed class IndexReader : IDisposable
{
    /// <summary>
    ///     Ceiling on one file listing, for the agent's glob and the operator's browse alike. Enough to
    ///     show every handler in a mid-sized service or browse a repository's whole source tree in one
    ///     page; past it the listing is not a thing to read, and the total in the answer says so.
    /// </summary>
    public const int MaxFiles = 2000;

    // The join is for the slug only; qualified_path already carries it as a prefix, but splitting a
    // string to recover what a column holds would be the worse choice.
    private const string FileColumns =
        "SELECT f.file_id, f.qualified_path, r.slug, f.line_count, f.size_bytes, f.skip_reason";

    private const string FileSource = "FROM files f JOIN repositories r USING (repo_id)";

    private readonly IndexLease _lease;

    // Loaded together, once, on first need: which repositories the build read and how it named their
    // files are one statement's worth of the same row set, and a read_file that only needs a line
    // range should not pay for either.
    private IReadOnlyList<IndexedRepository>? _repositories;
    private ProjectPaths? _paths;

    private IndexReader(IndexLease lease, string projectSlug)
    {
        _lease = lease;
        ProjectSlug = projectSlug;
    }

    /// <summary>The project the connection is bound to, named in every explanation so an agent on the wrong endpoint sees it.</summary>
    public string ProjectSlug { get; }

    /// <summary>
    ///     The repository the <c>repo</c> argument resolved to, in the spelling the index holds, or null
    ///     when the call covers every repository. Set by <see cref="OpenAsync" />; an unknown slug never
    ///     gets this far.
    /// </summary>
    public IndexedRepository? Repository { get; private set; }

    /// <summary>A connection already <c>USE</c>ing the project, for the searches that write their own SQL.</summary>
    public DuckDBConnection Connection => _lease.Connection;

    /// <summary>
    ///     Whether a full-text search can answer from this index: the file holds a BM25 index and this
    ///     process has the extension to query it. Both are asked, because they come apart: an index
    ///     built with full text survives a restart under <c>Index:SearchEngine=Substring</c>, and one
    ///     built without it is not given one by loading the extension later. The decision is per index,
    ///     not per process, and this is the one place it is made.
    /// </summary>
    public async Task<bool> HasFullTextAsync(CancellationToken cancellationToken)
    {
        if (!_lease.FullTextLoaded) return false;
        using var command = Connection.Query("SELECT fts_indexed FROM index_info", []);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Releases the lease as well as the connection, which is what lets a swap proceed.</summary>
    public void Dispose() => _lease.Dispose();

    /// <summary>
    ///     Opens the project's index for one call, restoring it from the durable copy first when a
    ///     replica's disk lost it (#9). <paramref name="repository" /> is the caller's <c>repo</c>
    ///     argument, matched case-insensitively the way a path is, so an agent quoting a slug from
    ///     memory in the wrong case is not told the repository does not exist; null or blank covers
    ///     every repository.
    /// </summary>
    public static async Task<IndexOpen> OpenAsync(ProjectIndexes indexes, string projectSlug, string? repository,
        CancellationToken cancellationToken)
    {
        var lease = await indexes.OpenAsync(projectSlug, cancellationToken);
        if (lease is null) return new IndexOpen.NoIndex(NoIndex(projectSlug));

        var reader = new IndexReader(lease, projectSlug);
        if (string.IsNullOrWhiteSpace(repository)) return new IndexOpen.Opened(reader);

        try
        {
            string wanted = repository.Trim();
            if (await reader.FindRepositoryAsync(wanted, cancellationToken) is { } found)
            {
                reader.Repository = found;
                return new IndexOpen.Opened(reader);
            }

            string explanation = await reader.UnknownRepositoryAsync(wanted, cancellationToken);
            reader.Dispose();
            return new IndexOpen.UnknownRepository($"{explanation} Drop `repo` to cover every repository.");
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     What the index holds, for a reader describing the project rather than reading from it. Null
    ///     when there is no index, and null when the file exists but has no <c>index_info</c> row: the
    ///     row is written last, so its absence is what an interrupted build leaves behind, and reading
    ///     the half-built tables as the project would be a wrong answer shaped like a right one.
    /// </summary>
    /// <param name="indexes">The attached indexes.</param>
    /// <param name="projectSlug">The project to describe.</param>
    /// <param name="restore">
    ///     Whether a project whose file is absent is restored from its durable copy first. True for a
    ///     read about one project, false for one that walks every project — the operator's list
    ///     touches all of them, and restoring every durable copy to draw one page is exactly what lazy
    ///     attach exists to avoid (#9). A project whose file the last shutdown wiped reads as not
    ///     indexed there until someone opens it.
    /// </param>
    /// <param name="cancellationToken">Threaded through the restore and the queries.</param>
    public static async Task<IndexStatus?> StatusAsync(ProjectIndexes indexes, string projectSlug, bool restore,
        CancellationToken cancellationToken)
    {
        using var lease = restore
            ? await indexes.OpenAsync(projectSlug, cancellationToken)
            : await indexes.PeekAsync(projectSlug, cancellationToken);
        if (lease is null) return null;

        // epoch() hands back seconds as a double, which is the one representation of a TIMESTAMPTZ that
        // does not depend on whether the ICU extension is loaded to decide the session time zone.
        using var command = lease.Connection.Query(
            "SELECT epoch(built_at) AS built_seconds, fts_indexed, single_repository FROM index_info", []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var builtAt = DateTimeOffset.FromUnixTimeSeconds((long)reader.Double("built_seconds"));
        bool ftsIndexed = reader.Flag("fts_indexed");
        bool singleRepository = reader.Flag("single_repository");
        return new IndexStatus(builtAt, ftsIndexed, singleRepository,
            await ReadRepositoriesAsync(lease.Connection, cancellationToken));
    }

    /// <summary>The one explanation every reader gives when there is nothing to read from.</summary>
    public static string NoIndex(string projectSlug) =>
        $"Project '{projectSlug}' has no index to read from right now: it was never built, or a refresh is still building the first one. "
        + $"Ask the operator to refresh it with POST /api/projects/{projectSlug}/refresh, or retry shortly.";

    /// <summary>What the last build read, in build order, which is what a path may name.</summary>
    public async Task<IReadOnlyList<IndexedRepository>> RepositoriesAsync(CancellationToken cancellationToken)
    {
        await LoadShapeAsync(cancellationToken);
        return _repositories!;
    }

    /// <summary>
    ///     How this index named its files, read from the index itself rather than from the control
    ///     database: a reader answers from the index alone, and an index names things the way the build
    ///     that wrote it was told to, which is the only shape its rows can be read in (ADR-0005,
    ///     ADR-0006).
    /// </summary>
    public async Task<ProjectPaths> PathsAsync(CancellationToken cancellationToken)
    {
        await LoadShapeAsync(cancellationToken);
        return _paths!;
    }

    /// <summary>The repository a slug names, in the spelling the index holds; null when none does.</summary>
    public async Task<IndexedRepository?> FindRepositoryAsync(string slug, CancellationToken cancellationToken)
    {
        var repositories = await RepositoriesAsync(cancellationToken);
        return repositories.FirstOrDefault(r => string.Equals(r.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The sentence for a slug that names no repository: the fact and what would have been valid,
    ///     without advice, because what to do next depends on where the slug came from — a <c>repo</c>
    ///     argument is dropped, the first segment of a path is corrected.
    /// </summary>
    public async Task<string> UnknownRepositoryAsync(string slug, CancellationToken cancellationToken) =>
        $"No repository '{slug}' in project '{ProjectSlug}'. Repositories: {await SlugsAsync(cancellationToken)}.";

    /// <summary>
    ///     What a path in this project is made of, for the message that says one was not. The two
    ///     shapes are described in one place so a tool cannot explain one project's naming in the
    ///     other's words (ADR-0006).
    /// </summary>
    public async Task<string> PathRuleAsync(CancellationToken cancellationToken) =>
        (await PathsAsync(cancellationToken)).SingleRepository
            ? "this project holds one repository, so a path is the path inside it."
            : $"a qualified path must start with a repository slug, then the path inside it. Repositories: {await SlugsAsync(cancellationToken)}.";

    /// <summary>
    ///     Compared lower-cased on both sides: git paths are case-sensitive, but an agent quoting a path
    ///     from memory gets the case wrong far more often than a repository holds two files differing
    ///     only by case. An exact match wins if both exist.
    /// </summary>
    public async Task<IndexedFile?> FindFileAsync(string qualifiedPath, CancellationToken cancellationToken)
    {
        using var command = Connection.Query($"""
                                              {FileColumns}
                                              {FileSource}
                                              WHERE lower(f.qualified_path) = lower($p)
                                              ORDER BY f.qualified_path = $p DESC
                                              LIMIT 1
                                              """, [new DuckDBParameter("p", qualifiedPath)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    /// <summary>
    ///     Whether this index holds any history at all. An index built before ADR-0007, or one whose
    ///     every repository failed to walk, has the tables and nothing in them — and "no commits
    ///     recorded" must never be answered as "this file was never changed", which reads as a fact.
    /// </summary>
    public async Task<bool> HasHistoryAsync(CancellationToken cancellationToken)
    {
        using var command = Connection.Query("SELECT count(*) > 0 FROM commits", []);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    ///     The commits of a repository, newest first — or of every repository when none is named. A page
    ///     of history, ordered by <c>commit_id</c> because it ascends with history by construction while
    ///     an author date does not (ADR-0007).
    /// </summary>
    public async Task<IReadOnlyList<RecordedChange>> CommitsAsync(string? repositorySlug, int limit, int skip,
        CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        string scope = "";
        if (repositorySlug is not null)
        {
            scope = "WHERE repo_slug = $r";
            parameters.Add(new DuckDBParameter("r", repositorySlug));
        }

        using var command = Connection.Query($"""
                                              SELECT sha, repo_slug, author_name, author_email, authored_at, subject
                                              FROM commits {scope}
                                              ORDER BY commit_id DESC
                                              LIMIT {limit} OFFSET {skip}
                                              """, parameters);
        return await ChangesAsync(command, cancellationToken);
    }

    /// <summary>
    ///     The commits that touched one path of one repository, newest first. Matched on the path as the
    ///     commit recorded it, so history stops where the file was last renamed — which is the half of
    ///     rename-following ADR-0007 does not pay for, and which the tool says out loud.
    /// </summary>
    public async Task<IReadOnlyList<RecordedChange>> FileHistoryAsync(string repositorySlug, string path, int limit,
        CancellationToken cancellationToken)
    {
        using var command = Connection.Query($"""
                                              SELECT c.sha, c.repo_slug, c.author_name, c.author_email,
                                                     c.authored_at, c.subject
                                              FROM commit_files cf JOIN commits c USING (commit_id)
                                              WHERE c.repo_slug = $r AND cf.path = $p
                                              ORDER BY c.commit_id DESC
                                              LIMIT {limit}
                                              """,
            [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", path)]);
        return await ChangesAsync(command, cancellationToken);
    }

    /// <summary>
    ///     Who last changed each line of a file, as runs rather than as one row per line: consecutive
    ///     lines sharing a commit are one entry, which is how blame reads and a fraction of the output.
    /// </summary>
    public async Task<IReadOnlyList<AttributedLines>> BlameAsync(long fileId, int first, int last,
        CancellationToken cancellationToken)
    {
        // The runs are rebuilt from lines rather than read from attribution, because attribution is
        // keyed by blob and holds the ranges of the whole file: a window of it would have to be clipped
        // here anyway, and a line the build could not attribute is absent there but present here.
        // Gaps and islands: within one commit, consecutive line numbers have a constant difference from
        // their position in that commit's lines, so that difference is the run. The window function is
        // computed in a subquery because it is evaluated after grouping and cannot appear in GROUP BY.
        // Lines with no commit fall in one NULL partition, which islands correctly for the same reason.
        using var command = Connection.Query("""
                                             SELECT min(line_number) AS start_line, max(line_number) AS end_line,
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
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var runs = new List<AttributedLines>();
        while (await reader.ReadAsync(cancellationToken))
            runs.Add(new AttributedLines(
                reader.GetInt32(reader.GetOrdinal("start_line")), reader.GetInt32(reader.GetOrdinal("end_line")),
                await reader.IsDBNullAsync(reader.GetOrdinal("sha"), cancellationToken)
                    ? null
                    : new AttributedBy(reader.Text("sha"), reader.Text("author_name"),
                        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")),
                        reader.Text("subject"))));
        return runs;
    }

    /// <summary>
    ///     One line saying which commits a file was first and last changed by, ready to print, or a
    ///     sentence saying there is none. A file whose history is absent and one that was never changed
    ///     must not read alike, so neither is an empty string.
    /// </summary>
    public async Task<string> FileSpanAsync(long fileId, CancellationToken cancellationToken)
    {
        var span = await FileCommitsAsync(fileId, cancellationToken);
        if (span.Last is null) return "  history: none recorded for this file\n";

        // Named "since"/"last changed" rather than "created"/"author": history begins where the file was
        // last renamed, so the first commit recorded for a path is often a move and not its origin.
        return string.Create(CultureInfo.InvariantCulture,
            $"  history: since {Short(span.First)}; last changed {Short(span.Last)}\n");

        static string Short(AttributedBy? by) => by is null
            ? "unknown"
            : string.Create(CultureInfo.InvariantCulture, $"{by.Sha[..8]} {by.AuthoredAt:yyyy-MM-dd} {by.AuthorName}");
    }

    /// <summary>
    ///     The commits a file was first and last changed by, both null where no history was imported for
    ///     it. One query for both, because they are two columns of the same row.
    /// </summary>
    public async Task<FileCommits> FileCommitsAsync(long fileId, CancellationToken cancellationToken)
    {
        using var command = Connection.Query("""
                                             SELECT first.sha AS first_sha, first.author_name AS first_author,
                                                    first.authored_at AS first_at, first.subject AS first_subject,
                                                    last.sha AS last_sha, last.author_name AS last_author,
                                                    last.authored_at AS last_at, last.subject AS last_subject
                                             FROM files f
                                             LEFT JOIN commits first ON first.commit_id = f.first_commit
                                             LEFT JOIN commits last ON last.commit_id = f.last_commit
                                             WHERE f.file_id = $f
                                             """, [new DuckDBParameter("f", fileId)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new FileCommits(null, null);
        return new FileCommits(Read(reader, "first"), Read(reader, "last"));

        static AttributedBy? Read(System.Data.Common.DbDataReader reader, string prefix) =>
            reader.IsNull(prefix + "_sha")
                ? null
                : new AttributedBy(reader.Text(prefix + "_sha"), reader.Text(prefix + "_author"),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(prefix + "_at")),
                    reader.Text(prefix + "_subject"));
    }

    /// <summary>How many commits are recorded, for a repository or for the whole project, so a page can say how many there are.</summary>
    public async Task<long> CommitCountAsync(string? repositorySlug, CancellationToken cancellationToken)
    {
        var (scope, parameters) = CommitScope(repositorySlug);
        using var command = Connection.Query($"SELECT count(*) FROM commits {scope}", parameters);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    /// <summary>
    ///     A page of the change log, newest first, with each commit's body and the sums of what it did.
    ///     Ordered by <c>commit_id</c> for the reason <see cref="CommitsAsync" /> gives. The sums are cast
    ///     because DuckDB widens <c>sum</c> of an INTEGER to HUGEINT, which the driver hands back as a
    ///     BigInteger.
    /// </summary>
    public async Task<IReadOnlyList<LoggedCommit>> ChangeLogAsync(string? repositorySlug, int limit, int skip,
        CancellationToken cancellationToken)
    {
        var (scope, parameters) = CommitScope(repositorySlug);
        using var command = Connection.Query($"""
                                              SELECT c.sha, c.repo_slug, c.author_name, c.author_email, c.authored_at,
                                                     c.subject, c.body,
                                                     count(cf.path)::INTEGER AS files_changed,
                                                     coalesce(sum(cf.added), 0)::INTEGER AS added,
                                                     coalesce(sum(cf.deleted), 0)::INTEGER AS deleted
                                              FROM commits c LEFT JOIN commit_files cf USING (commit_id)
                                              {scope}
                                              GROUP BY c.commit_id, c.sha, c.repo_slug, c.author_name, c.author_email,
                                                       c.authored_at, c.subject, c.body
                                              ORDER BY c.commit_id DESC
                                              LIMIT {limit} OFFSET {skip}
                                              """, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var commits = new List<LoggedCommit>();
        while (await reader.ReadAsync(cancellationToken))
            commits.Add(new LoggedCommit(reader.Text("sha"), reader.Text("repo_slug"), reader.Text("author_name"),
                reader.Text("author_email"), reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")),
                reader.Text("subject"), reader.Text("body"), reader.Int32("files_changed"), reader.Int32("added"),
                reader.Int32("deleted")));
        return commits;
    }

    /// <summary>
    ///     The paths one commit touched, with the qualified path of each that is still at HEAD. Matched
    ///     by full SHA: the caller got it from a listing and has no reason to abbreviate it. Empty for a
    ///     SHA the index does not hold, which the caller tells from a commit that touched nothing by
    ///     asking the listing — a root commit with files is the ordinary case, one with none is not.
    /// </summary>
    public async Task<IReadOnlyList<CommitFile>> CommitFilesAsync(string sha, CancellationToken cancellationToken)
    {
        using var command = Connection.Query("""
                                             SELECT cf.path, cf.change_kind, cf.added, cf.deleted, f.qualified_path
                                             FROM commits c JOIN commit_files cf USING (commit_id)
                                             LEFT JOIN repositories r ON r.slug = c.repo_slug
                                             LEFT JOIN files f ON f.repo_id = r.repo_id AND f.path = cf.path
                                             WHERE c.sha = $sha
                                             ORDER BY cf.path
                                             """, [new DuckDBParameter("sha", sha)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var files = new List<CommitFile>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new CommitFile(reader.Text("path"), reader.Text("change_kind"), reader.Int32("added"),
                reader.Int32("deleted"), reader.IsNull("qualified_path") ? null : reader.Text("qualified_path")));
        return files;
    }

    /// <summary>The WHERE clause and its parameter for one repository's commits, or neither for the project's.</summary>
    private static (string Scope, List<DuckDBParameter> Parameters) CommitScope(string? repositorySlug) =>
        repositorySlug is null
            ? ("", [])
            : ("WHERE repo_slug = $r", [new DuckDBParameter("r", repositorySlug)]);

    private static async Task<IReadOnlyList<RecordedChange>> ChangesAsync(DuckDBCommand command,
        CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var changes = new List<RecordedChange>();
        while (await reader.ReadAsync(cancellationToken))
            changes.Add(new RecordedChange(reader.Text("sha"), reader.Text("repo_slug"), reader.Text("author_name"),
                reader.Text("author_email"),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")), reader.Text("subject")));
        return changes;
    }

    /// <summary>Files anywhere in the project with this leaf name, for a "did you mean" after a miss.</summary>
    public async Task<IReadOnlyList<string>> FilesNamedAsync(string name, int limit,
        CancellationToken cancellationToken)
    {
        using var command = Connection.Query(
            $"SELECT qualified_path FROM files WHERE lower(name) = lower($n) ORDER BY qualified_path LIMIT {limit}",
            [new DuckDBParameter("n", name)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.Text("qualified_path"));
        return result;
    }

    /// <summary>
    ///     Lines <paramref name="first" /> to <paramref name="last" /> inclusive, in order; fewer when the file ends
    ///     first.
    /// </summary>
    public async Task<IReadOnlyList<string>> LinesAsync(long fileId, int first, int last,
        CancellationToken cancellationToken)
    {
        using var command = Connection.Query("""
                                             SELECT content FROM lines
                                             WHERE file_id = $f AND line_number BETWEEN $a AND $b
                                             ORDER BY line_number
                                             """,
            [new DuckDBParameter("f", fileId), new DuckDBParameter("a", first), new DuckDBParameter("b", last)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.Text("content"));
        return result;
    }

    /// <summary>
    ///     Case-insensitive <c>GLOB</c> over the qualified path, within <see cref="Repository" /> when
    ///     one was resolved. The window count rides along with the rows so one statement yields both
    ///     the total and the page. <paramref name="limit" /> is clamped to <see cref="MaxFiles" />.
    /// </summary>
    public async Task<GlobResult> GlobAsync(string glob, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxFiles);
        var parameters = new List<DuckDBParameter> { new("g", glob.ToLowerInvariant()) };
        string scope = "";
        if (Repository is not null)
        {
            scope = " AND r.slug = $r";
            parameters.Add(new DuckDBParameter("r", Repository.Slug));
        }

        var files = new List<IndexedFile>();
        int total = 0;
        using (var command = Connection.Query($"""
                                               {FileColumns}, count(*) OVER () AS total
                                               {FileSource}
                                               WHERE lower(f.qualified_path) GLOB $g{scope}
                                               ORDER BY f.qualified_path
                                               LIMIT {limit}
                                               """, parameters))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                files.Add(ReadFile(reader));
                total = (int)reader.Int64("total");
            }
        }

        int? elsewhere = null;
        if (total == 0 && Repository is not null)
        {
            using var command = Connection.Query("SELECT count(*) FROM files f WHERE lower(f.qualified_path) GLOB $g",
                [new DuckDBParameter("g", glob.ToLowerInvariant())]);
            elsewhere = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        return new GlobResult(total, files, elsewhere);
    }

    /// <summary>
    ///     The project as a tree, <paramref name="depth" /> levels deep: the repositories at the root,
    ///     or the subdirectories and files of <paramref name="location" /> inside one of them. The list
    ///     reads as <c>tree</c> prints: a directory is followed by its own entries before its siblings,
    ///     directories before files at each level, each alphabetical. Directories are not rows in the
    ///     index — <c>files.directory</c> holds each file's whole repository-relative directory — so a
    ///     level is the distinct first segment below the prefix, aggregated over everything beneath it.
    ///     Children are formatted through the same <see cref="ProjectPaths" /> that parsed the level, so
    ///     a listing cannot be spelled differently from what it links to. Empty means the location is
    ///     not a directory: git has no empty directories, so neither has the index.
    /// </summary>
    /// <param name="location">
    ///     Null asks for the repositories themselves, which is the root of a multi-repository project.
    ///     A single-repository project has no such level, and <see cref="ProjectPaths.Parse" /> never
    ///     hands back null for one. The repository slug is used as given; the caller resolves it.
    /// </param>
    /// <param name="depth">How many levels to descend, at least 1. The web tree view asks for one.</param>
    /// <param name="cancellationToken">Threaded through to every DuckDB command.</param>
    public async Task<IReadOnlyList<TreeItem>> TreeAsync(QualifiedPath? location, int depth,
        CancellationToken cancellationToken)
    {
        var entries = new List<TreeItem>();
        await CollectAsync(location, depth, entries, cancellationToken);
        return entries;
    }

    /// <summary>
    ///     One query per directory visited rather than one recursive query: a listing is bounded by
    ///     what an agent can read, and DuckDB answers a level on a local file in well under a
    ///     millisecond, so the simpler shape costs nothing anyone would measure.
    /// </summary>
    private async Task CollectAsync(QualifiedPath? location, int depth, List<TreeItem> entries,
        CancellationToken cancellationToken)
    {
        var level = location is null
            ? await RepositoryLevelAsync(cancellationToken)
            : await DirectoryLevelAsync(location, cancellationToken);
        foreach (var item in level)
        {
            entries.Add(item);
            // A file has no Files count; a repository or directory does, and is what depth descends into.
            if (depth <= 1 || item.Files is null) continue;
            var below = location is null
                ? new QualifiedPath(item.Name, "")
                : location with
                {
                    PathInRepository = location.PathInRepository.Length == 0
                        ? item.Name
                        : location.PathInRepository + "/" + item.Name
                };
            await CollectAsync(below, depth - 1, entries, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<TreeItem>> DirectoryLevelAsync(QualifiedPath location,
        CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        string repositorySlug = location.RepositorySlug;
        string directory = location.PathInRepository;

        // "" at a repository root, "src/" below one. Every directory under this level starts with it,
        // and the next segment begins where it ends. The length is taken in SQL rather than in C#
        // because a .NET string counts UTF-16 units and `substr` counts characters, which part ways on
        // any path outside the BMP.
        string prefix = directory.Length == 0 ? "" : directory + "/";
        var entries = new List<TreeItem>();

        using (var command = Connection.Query("""
                                              SELECT split_part(substr(f.directory, length($p) + 1), '/', 1) AS segment,
                                                     CAST(count(*) AS BIGINT) AS files,
                                                     CAST(sum(f.line_count) AS BIGINT) AS lines,
                                                     CAST(sum(f.size_bytes) AS BIGINT) AS bytes
                                              FROM files f JOIN repositories r USING (repo_id)
                                              WHERE r.slug = $r AND f.directory LIKE $p || '%' AND f.directory <> $d
                                              GROUP BY segment
                                              ORDER BY segment
                                              """,
                   [
                       new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", prefix),
                       new DuckDBParameter("d", directory)
                   ]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string segment = reader.Text("segment");
                entries.Add(new TreeItem(segment, paths.Format(repositorySlug, prefix + segment),
                    (int)reader.Int64("files"), reader.Int64("lines"), reader.Int64("bytes"), null));
            }
        }

        using (var command = Connection.Query("""
                                              SELECT f.name, f.qualified_path, f.line_count, f.size_bytes, f.skip_reason
                                              FROM files f JOIN repositories r USING (repo_id)
                                              WHERE r.slug = $r AND f.directory = $d
                                              ORDER BY f.name
                                              """,
                   [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("d", directory)]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new TreeItem(reader.Text("name"), reader.Text("qualified_path"), null,
                    reader.Int32("line_count"), reader.Int64("size_bytes"), reader.TextOrNull("skip_reason")));
        }

        return entries;
    }

    /// <summary>Extensions in the project, or in <see cref="Repository" /> when one was resolved, most files first.</summary>
    public async Task<IReadOnlyList<ExtensionCount>> ExtensionsAsync(CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        string scope = "";
        if (Repository is not null)
        {
            scope = " WHERE r.slug = $r";
            parameters.Add(new DuckDBParameter("r", Repository.Slug));
        }

        using var command = Connection.Query($"""
                                              SELECT f.extension,
                                                     count(*) AS files,
                                                     sum(f.line_count)::BIGINT AS lines,
                                                     count(f.skip_reason) AS skipped
                                              FROM files f JOIN repositories r USING (repo_id){scope}
                                              GROUP BY f.extension
                                              ORDER BY count(*) DESC, f.extension
                                              """, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ExtensionCount>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ExtensionCount(reader.Text("extension"), (int)reader.Int64("files"),
                // sum() yields a HUGEINT, which the reader surfaces as BigInteger; the cast above keeps it a long.
                reader.Int64("lines"), (int)reader.Int64("skipped")));
        return result;
    }

    /// <summary>
    ///     The root of a project: its repositories, which are what qualified paths begin with. The file
    ///     and line counts are the ones the build recorded on <c>repositories</c>, not a second count
    ///     over <c>files</c>: two definitions of "how many files are in this repository" would disagree
    ///     the moment a build skips something, and the tree would then contradict the project page. Only
    ///     the byte total has to be summed. A repository indexed from an empty tree still belongs here,
    ///     hence the left join and the coalesce — it is a repository with no files, not an absent one.
    /// </summary>
    private async Task<IReadOnlyList<TreeItem>> RepositoryLevelAsync(CancellationToken cancellationToken)
    {
        using var command = Connection.Query("""
                                             SELECT r.slug,
                                                    CAST(r.file_count AS BIGINT) AS files,
                                                    CAST(r.line_count AS BIGINT) AS lines,
                                                    CAST(coalesce(sum(f.size_bytes), 0) AS BIGINT) AS bytes
                                             FROM repositories r LEFT JOIN files f USING (repo_id)
                                             GROUP BY r.slug, r.file_count, r.line_count
                                             ORDER BY r.slug
                                             """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<TreeItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.Text("slug");
            entries.Add(new TreeItem(slug, slug, (int)reader.Int64("files"), reader.Int64("lines"),
                reader.Int64("bytes"), null));
        }

        return entries;
    }

    private async Task<string> SlugsAsync(CancellationToken cancellationToken) =>
        string.Join(", ", (await RepositoriesAsync(cancellationToken)).Select(r => r.Slug));

    /// <summary>
    ///     One statement for the repositories and the naming rule. Both halves answer empty on an index
    ///     still being filled — no <c>index_info</c> row until the build completes, no repositories
    ///     until the first one is ingested — and then the shape falls back to a multi-repository
    ///     project anchored on the project slug, which is the only name there is to anchor on.
    /// </summary>
    private async Task LoadShapeAsync(CancellationToken cancellationToken)
    {
        if (_repositories is not null) return;

        using var command = Connection.Query("""
                                             SELECT r.slug, r.url, r.head_commit, r.file_count, r.line_count,
                                                    (SELECT single_repository FROM index_info) AS single_repository
                                             FROM repositories r
                                             ORDER BY r.repo_id
                                             """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var repositories = new List<IndexedRepository>();
        bool singleRepository = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            repositories.Add(ReadRepository(reader));
            singleRepository = reader.FlagOrFalse("single_repository");
        }

        _paths = new ProjectPaths(singleRepository, repositories.Count > 0 ? repositories[0].Slug : ProjectSlug);
        _repositories = repositories;
    }

    private static async Task<IReadOnlyList<IndexedRepository>> ReadRepositoriesAsync(DuckDBConnection connection,
        CancellationToken cancellationToken)
    {
        // The history each repository has, joined on the slug rather than the id, because that is what
        // commits records (ADR-0007). A repository with no commits keeps a null newest commit and a zero
        // count, which the page draws as "no history" rather than as a repository that never changed.
        using var command = connection.Query(
            """
            SELECT r.slug, r.url, r.head_commit, r.file_count, r.line_count,
                   coalesce(h.commits, 0) AS commits, h.sha, h.author_name, h.authored_at, h.subject
            FROM repositories r
            LEFT JOIN (SELECT repo_slug, count(*) AS commits,
                              argMax(sha, commit_id) AS sha, argMax(author_name, commit_id) AS author_name,
                              argMax(authored_at, commit_id) AS authored_at, argMax(subject, commit_id) AS subject
                       FROM commits GROUP BY repo_slug) h ON h.repo_slug = r.slug
            ORDER BY r.repo_id
            """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<IndexedRepository>();
        // The history columns are read only here. The other caller, LoadShapeAsync, is the path every
        // tool takes to learn the repository names, and it has no use for a join onto commits.
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRepository(reader) with
            {
                Commits = reader.Int64("commits"),
                NewestCommit = reader.IsNull("sha")
                    ? null
                    : new AttributedBy(reader.Text("sha"), reader.Text("author_name"),
                        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")),
                        reader.Text("subject"))
            });
        return result;
    }

    private static IndexedRepository ReadRepository(DbDataReader reader) => new(
        reader.Text("slug"), reader.Text("url"), reader.Text("head_commit"),
        reader.Int32("file_count"), reader.Int64("line_count"));

    /// <summary>
    ///     Reads the columns <see cref="FileColumns" /> selects, by name: the glob query appends a
    ///     window count to that list, so a positional read here would break the moment another caller
    ///     prepends anything to its own projection.
    /// </summary>
    private static IndexedFile ReadFile(DbDataReader reader) => new(
        reader.Int64("file_id"), reader.Text("qualified_path"), reader.Text("slug"),
        reader.Int32("line_count"), reader.Int64("size_bytes"), reader.TextOrNull("skip_reason"));
}
