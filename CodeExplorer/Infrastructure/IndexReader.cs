using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     A row of <c>files</c>. <see cref="SkipReason" /> is set when the file is committed but has no lines in the
///     index. <see cref="Module" /> is what the file declares itself to be — a C# namespace, a Delphi
///     unit — or null where it declares none (#55); it is on this row rather than fetched when wanted,
///     because every caller that wants it has already read this row to find the file at all.
/// </summary>
public sealed record IndexedFile(
    long FileId,
    string QualifiedPath,
    string RepositorySlug,
    string PathInRepository,
    int LineCount,
    long SizeBytes,
    string? SkipReason,
    string? Module);

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

/// <summary>
///     One file of a churn ranking: how many commits of the window touched it and what they did to
///     it. <see cref="QualifiedPath" /> is how the project names that path (ADR-0006) and is always
///     set, because a window ranks paths a later commit deleted or renamed away and those have to be
///     named too; <see cref="AtHead" /> is what says whether there is still a file there to read.
/// </summary>
public sealed record ChurnedFile(
    string QualifiedPath,
    string RepositorySlug,
    bool AtHead,
    int Commits,
    long Added,
    long Deleted);

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

/// <summary>
///     Which repositories of a project an answer drawn from history can speak for, and which it
///     cannot. Both sides, because "says which is which" is the point: naming only the repositories
///     that were not walked leaves a reader to infer the rest from a ranking, which is the inference
///     the caveat exists to prevent (CONTEXT.md, History).
///     <see cref="NothingToSay" /> where the question does not arise — a project of one repository, an
///     answer already scoped to one, or every repository walked — so a caller tests one thing.
/// </summary>
public sealed record HistoryCoverage(IReadOnlyList<string> With, IReadOnlyList<string> Without)
{
    public static readonly HistoryCoverage NothingToSay = new([], []);
}

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
///     A directory an agent named, resolved to what the index holds. <see cref="Repository" /> is null
///     at the project level, which only a multi-repository project has; <see cref="PathInRepository" />
///     is empty at a repository's own root. <see cref="QualifiedPath" /> is how this project spells it
///     (ADR-0006), which is what a reply names it by.
/// </summary>
public sealed record IndexedDirectory(IndexedRepository? Repository, string PathInRepository, string QualifiedPath);

/// <summary>
///     The one way a project's index is read. <see cref="OverIndexAsync{T}" /> is the seam every reader
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
        "SELECT f.file_id, f.qualified_path, r.slug, f.path, f.line_count, f.size_bytes, f.skip_reason, f.module";

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
    ///     when the call covers every repository. Set by <see cref="OverIndexAsync{T}" />; an unknown slug never
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
    ///     Opens the project's index for one call and hands it to <paramref name="read" />, or hands
    ///     <paramref name="refused" /> the <see cref="Problem" /> saying why it could not: never built,
    ///     or a <c>repo</c> that names no repository in it. A semantic failure is an answer, never an
    ///     exception (CODING_STANDARDS), and every reader — MCP tool, operator endpoint, search — is
    ///     refused in one wording rather than eleven.
    ///     The open, the refusal and the dispose are one contract, and it lives here because twenty-four
    ///     callers had each spelled it out: a second copy of it is a second place for it to be got wrong,
    ///     and a reader kept past the call is what holds up a swap (ADR-0003). The index is restored from
    ///     its durable copy first when a replica's disk lost it (#9). <paramref name="repository" /> is
    ///     the caller's <c>repo</c> argument, matched case-insensitively the way a path is, so an agent
    ///     quoting a slug from memory in the wrong case is not told the repository does not exist; null
    ///     or blank covers every repository, and the one it names is <see cref="Repository" />.
    /// </summary>
    public static async Task<T> OverIndexAsync<T>(ProjectIndexes indexes, string projectSlug, string? repository,
        Func<IndexReader, CancellationToken, Task<T>> read, Func<Problem, T> refused,
        CancellationToken cancellationToken)
    {
        var lease = await indexes.OpenAsync(projectSlug, cancellationToken);
        if (lease is null) return refused(new Problem(NoIndex(projectSlug), ProblemKind.NoIndex));

        using var reader = new IndexReader(lease, projectSlug);
        if (!string.IsNullOrWhiteSpace(repository))
        {
            string wanted = repository.Trim();
            if (await reader.FindRepositoryAsync(wanted, cancellationToken) is not { } found)
                return refused(new Problem(
                    $"{await reader.UnknownRepositoryAsync(wanted, cancellationToken)} Drop `repo` to cover every repository."));
            reader.Repository = found;
        }

        return await read(reader, cancellationToken);
    }

    /// <summary>The same for a caller whose answer is an <see cref="Outcome" />, which a problem already is.</summary>
    public static Task<Outcome> OverIndexAsync(ProjectIndexes indexes, string projectSlug, string? repository,
        Func<IndexReader, CancellationToken, Task<Outcome>> read, CancellationToken cancellationToken) =>
        OverIndexAsync(indexes, projectSlug, repository, read, problem => problem, cancellationToken);

    /// <summary>
    ///     Opens the project's index, resolves <paramref name="path" /> the way <see cref="LocateAsync" />
    ///     does, and hands <paramref name="read" /> the file — or <paramref name="refused" /> the sentence
    ///     saying why there is none. Every tool that takes one file begins this way.
    /// </summary>
    public static Task<T> OverFileAsync<T>(ProjectIndexes indexes, string projectSlug, string path, bool suggestions,
        Func<IndexReader, IndexedFile, CancellationToken, Task<T>> read, Func<Problem, T> refused,
        CancellationToken cancellationToken) =>
        OverIndexAsync(indexes, projectSlug, null, async (index, token) =>
        {
            var (file, problem) = await index.LocateAsync(path, suggestions, token);
            return file is null ? refused(problem!) : await read(index, file, token);
        }, refused, cancellationToken);

    /// <summary>The same for a caller whose answer is an <see cref="Outcome" />.</summary>
    public static Task<Outcome> OverFileAsync(ProjectIndexes indexes, string projectSlug, string path, bool suggestions,
        Func<IndexReader, IndexedFile, CancellationToken, Task<Outcome>> read, CancellationToken cancellationToken) =>
        OverFileAsync(indexes, projectSlug, path, suggestions, read, problem => problem, cancellationToken);

    /// <summary>
    ///     Opens the project's index, resolves <paramref name="path" /> the way
    ///     <see cref="LocateDirectoryAsync" /> does, and hands <paramref name="read" /> the directory —
    ///     or <paramref name="refused" /> the sentence saying why there is none.
    /// </summary>
    public static Task<T> OverDirectoryAsync<T>(ProjectIndexes indexes, string projectSlug, string? path,
        Func<IndexReader, IndexedDirectory, CancellationToken, Task<T>> read, Func<Problem, T> refused,
        CancellationToken cancellationToken) =>
        OverIndexAsync(indexes, projectSlug, null, async (index, token) =>
        {
            var (directory, problem) = await index.LocateDirectoryAsync(path, token);
            return directory is null ? refused(problem!) : await read(index, directory, token);
        }, refused, cancellationToken);

    /// <summary>The same for a caller whose answer is an <see cref="Outcome" />.</summary>
    public static Task<Outcome> OverDirectoryAsync(ProjectIndexes indexes, string projectSlug, string? path,
        Func<IndexReader, IndexedDirectory, CancellationToken, Task<Outcome>> read, CancellationToken cancellationToken) =>
        OverDirectoryAsync(indexes, projectSlug, path, read, problem => problem, cancellationToken);

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
    ///     The file a qualified path names, or the sentence saying why it names none. A path cannot
    ///     be read without knowing how the project names its files (ADR-0006), and an agent that got
    ///     it wrong needs the rule rather than an empty answer — so the misses are answers here and
    ///     not exceptions, the way CODING_STANDARDS asks.
    ///     It is on the reader rather than in each tool because three tools were resolving a path by
    ///     spelling this sequence out, and the three refusal sentences an agent reads had begun to
    ///     drift apart. Most callers reach it through <see cref="OverFileAsync{T}" />; this is for the
    ///     one that resolves several paths over one open.
    /// </summary>
    /// <param name="path">The qualified path an agent wrote.</param>
    /// <param name="suggestions">
    ///     Whether a miss should offer the files elsewhere in the project with the same leaf name.
    ///     Worth it where the agent chose the path, and noise where it came from a list this
    ///     produced.
    /// </param>
    /// <param name="cancellationToken">Threaded to every command this runs.</param>
    public async Task<(IndexedFile? File, Problem? Problem)> LocateAsync(string path, bool suggestions,
        CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        var qualified = paths.Parse(path);
        if (qualified is null || qualified.PathInRepository.Length == 0)
            return (null, new Problem(
                $"'{path}' names no file: {await PathRuleAsync(cancellationToken)} Write it like `{paths.Example()}`."));

        var repository = await FindRepositoryAsync(qualified.RepositorySlug, cancellationToken);
        if (repository is null)
            return (null, new Problem(
                $"{await UnknownRepositoryAsync(qualified.RepositorySlug, cancellationToken)} The first path segment must be one of these."));

        string spelled = paths.Format(qualified with { RepositorySlug = repository.Slug });
        if (await FindFileAsync(spelled, cancellationToken) is { } file) return (file, null);

        string explanation = $"No indexed file '{spelled}' in repository '{repository.Slug}' of project "
                             + $"'{ProjectSlug}'. ";
        if (!suggestions)
            return (null, new Problem(explanation + "Use glob or list_tree to locate it.", ProblemKind.Missing));

        string name = qualified.PathInRepository[(qualified.PathInRepository.LastIndexOf('/') + 1)..];
        var similar = await FilesNamedAsync(name, MaxSuggestions, cancellationToken);
        return (null, new Problem(explanation + (similar.Count > 0
                ? $"Did you mean {string.Join(" or ", similar)}? Otherwise use glob or list_tree to locate it."
                : "Use glob or list_tree to locate it; the path is case-insensitive here but must otherwise match the committed path."),
            ProblemKind.Missing));
    }

    /// <summary>
    ///     The directory a path names, or the sentence saying why it names none. A blank path is the
    ///     project level, and a repository slug alone is that repository's root. Whether
    ///     the directory holds anything is not asked: that is a fact about the tree, and the tree's
    ///     reader is the one place to say "this is a file" or "nothing is here" in one wording.
    ///     It exists for the reason <see cref="LocateAsync" /> does: three tools had spelled out the
    ///     parse, the repository match and the respelling for themselves.
    /// </summary>
    public async Task<(IndexedDirectory? Directory, Problem? Problem)> LocateDirectoryAsync(string? path,
        CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        // Null from Parse is the project level, which only a multi-repository project has and which a
        // non-blank path cannot mean: one that parses to it — a bare `/` — names nothing.
        // Blank is the project level whatever the project's shape; what a single-repository project,
        // which has no such level, makes of it is the caller's question (ADR-0006).
        if (string.IsNullOrWhiteSpace(path)) return (new IndexedDirectory(null, "", ""), null);
        var qualified = paths.Parse(path);
        if (qualified is null)
            return (null, new Problem($"'{path}' names no directory: {await PathRuleAsync(cancellationToken)}"));

        var repository = await FindRepositoryAsync(qualified.RepositorySlug, cancellationToken);
        if (repository is null)
            return (null, new Problem(
                $"{await UnknownRepositoryAsync(qualified.RepositorySlug, cancellationToken)} The first path segment must be one of these."));

        string spelled = paths.Format(qualified with { RepositorySlug = repository.Slug });
        return (new IndexedDirectory(repository, qualified.PathInRepository, spelled), null);
    }

    /// <summary>A "did you mean" longer than this is a glob result, and glob is the better tool for it.</summary>
    private const int MaxSuggestions = 5;

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
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug);
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
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug);
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

    /// <summary>
    ///     The repositories of this project that have no imported history, in build order — the one
    ///     place the rule lives, because an answer covering half a project reads as covering all of it
    ///     and every surface that ranks over history has to say so in its own words (CONTEXT.md,
    ///     History).
    ///     Empty where the question does not arise, which is the caller's answer and not its judgement:
    ///     a call already scoped to one repository is not speaking for the others, and a project of one
    ///     repository has nothing to contrast with.
    ///     Not <see cref="RepositoriesAsync" />'s rows, whose <c>Commits</c> is filled only on the
    ///     <see cref="StatusAsync" /> path and is silently zero on this one — reading it here would
    ///     report every repository as historyless.
    /// </summary>
    /// <param name="scopedTo">The repository the caller narrowed to, or null for the whole project.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public async Task<HistoryCoverage> HistoryCoverageAsync(string? scopedTo,
        CancellationToken cancellationToken)
    {
        if (scopedTo is not null) return HistoryCoverage.NothingToSay;

        // EXISTS rather than a count: the question is whether a repository was walked at all, and a
        // semi-join stops at the first commit where count(*) reads every one of them. Matched on the
        // slug, not the id, because that is what commits records (ADR-0007).
        using var command = Connection.Query("""
                                             SELECT r.slug,
                                                    EXISTS (SELECT 1 FROM commits c WHERE c.repo_slug = r.slug)
                                                        AS walked
                                             FROM repositories r
                                             ORDER BY r.repo_id
                                             """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var all = new List<(string Slug, bool Walked)>();
        while (await reader.ReadAsync(cancellationToken)) all.Add((reader.Text("slug"), reader.Flag("walked")));
        // One repository cannot be contrasted with another, so there is nothing to say about it here;
        // that a project has no history at all is a different sentence, said before this is reached.
        if (all.Count < 2) return HistoryCoverage.NothingToSay;

        var without = all.Where(r => !r.Walked).Select(r => r.Slug).ToList();
        return without.Count == 0
            ? HistoryCoverage.NothingToSay
            : new HistoryCoverage(all.Where(r => r.Walked).Select(r => r.Slug).ToList(), without);
    }

    /// <summary>
    ///     The window reaching <paramref name="days" /> back from the newest commit recorded in scope,
    ///     or null when the scope holds no commit at all — a repository whose history could not be
    ///     walked, which is not the same as one nobody changed and must not be answered as one.
    /// </summary>
    public Task<HistoryWindow?> WindowAsync(int days, string? repositorySlug,
        CancellationToken cancellationToken) =>
        IndexQueries.WindowAsync(Connection, days, repositorySlug, cancellationToken);

    /// <summary>
    ///     The files a window's commits touched, most commits first: the first read of the commit
    ///     tables that is about more than one file's history.
    ///     The paths come back spelled the way the project spells them, rather than each caller
    ///     formatting the pair itself — the rule is ADR-0006's and the reader is what holds the
    ///     <see cref="ProjectPaths" /> that knows it.
    /// </summary>
    /// <param name="window">The span to count over, inclusive at both ends.</param>
    /// <param name="repositorySlug">One repository, or null for every one in the project.</param>
    /// <param name="directoryInRepository">
    ///     A directory inside that repository to count under, or null for all of it. Meaningless
    ///     without <paramref name="repositorySlug" />, because a directory of one repository is not a
    ///     directory of another; callers resolve both from the one qualified path they were given.
    /// </param>
    /// <param name="limit">How many files to return.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public async Task<IReadOnlyList<ChurnedFile>> ChurnAsync(HistoryWindow window, string? repositorySlug,
        string? directoryInRepository, int limit, CancellationToken cancellationToken) =>
        await IndexQueries.RankAsync(Connection, await PathsAsync(cancellationToken), window, repositorySlug,
            directoryInRepository, limit, cancellationToken);

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
    ///     coupling, they are one commit. Excluding them is the caller's to explain, which is why the
    ///     count of what was excluded comes back rather than being quietly dropped.
    ///     Here rather than in <see cref="IndexQueries" />: that class is for the reads a build makes
    ///     too, and no build pairs anything.
    /// </summary>
    /// <param name="window">The span to pair over, inclusive at both ends.</param>
    /// <param name="repositorySlug">The anchor's repository; a commit touches one, so pairing never crosses one.</param>
    /// <param name="pathInRepository">The anchor file, repository-relative, as <c>commit_files</c> records it.</param>
    /// <param name="maxCommitPaths">The most paths a commit may touch and still be paired.</param>
    /// <param name="limit">How many co-changed files to return.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public async Task<CoChanges> CoChangedAsync(HistoryWindow window, string repositorySlug,
        string pathInRepository, int maxCommitPaths, int limit, CancellationToken cancellationToken)
    {
        // Epoch seconds rather than timestamp parameters, for the reason IndexQueries compares them
        // that way: it keeps the comparison off the session time zone and a DateTimeOffset out of the
        // driver's parameter mapping.
        var parameters = new List<DuckDBParameter>
        {
            new("since", window.Since.ToUnixTimeSeconds()),
            new("until", window.Until.ToUnixTimeSeconds()),
            new("r", repositorySlug),
            new("p", pathInRepository),
            new("c", maxCommitPaths)
        };

        using var command = Connection.Query($"""
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
                                              -- already satisfies both. MATERIALIZED because two CTEs
                                              -- below read this one, and inlined it would be a second
                                              -- scan of commit_files to produce the same rows.
                                              touched AS MATERIALIZED (
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
                                                  -- the reason IndexQueries spells its ORDER BY out.
                                                  ORDER BY count(*) DESC, t.path
                                                  -- Inlined and not parameterised: it is an int the
                                                  -- caller has already clamped to a range, so there is
                                                  -- nothing to escape, and the churn ranking inlines
                                                  -- its own the same way.
                                                  LIMIT {limit})
                                              -- LEFT JOIN ON TRUE so the counts survive an empty
                                              -- ranking: a file that moves alone still has to say how
                                              -- many commits it was looked at over. The cross join
                                              -- does not carry ranked's order, so the outer ORDER BY
                                              -- is what makes the ranking a ranking.
                                              SELECT counts.commits, counts.paired, ranked.path, ranked.shared,
                                                     {IndexQueries.AtHeadExists("$r")} AS at_head
                                              FROM counts LEFT JOIN ranked ON TRUE
                                              ORDER BY ranked.shared DESC, ranked.path
                                              """, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var paths = await PathsAsync(cancellationToken);
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
    ///     The overview the build stored with this index (#51): one row, no joins and no aggregates, so
    ///     a caller orienting itself pays a row read rather than five passes over <c>files</c> and the
    ///     commit tables.
    ///     Always there for an index a reader can be opened on: the build writes the row before
    ///     <c>index_info</c>, and a durable copy from a schema without it is rebuilt rather than
    ///     restored. A missing row is therefore an index this build cannot read, which is the same
    ///     infrastructure failure an unreadable document is and throws the same way — not a state the
    ///     callers branch on.
    /// </summary>
    public async Task<IndexOverview> OverviewAsync(CancellationToken cancellationToken)
    {
        using var command = Connection.Query("SELECT document FROM project_overview LIMIT 1", []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw IndexOverview.Unreadable($"the index of project '{ProjectSlug}' holds no overview row");

        return IndexOverview.FromDocument(reader.Text("document"));
    }

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
        reader.Int64("file_id"), reader.Text("qualified_path"), reader.Text("slug"), reader.Text("path"),
        reader.Int32("line_count"), reader.Int64("size_bytes"), reader.TextOrNull("skip_reason"),
        reader.TextOrNull("module"));
}
