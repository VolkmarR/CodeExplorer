namespace CodeExplorer;

/// <summary>
///     One file as the file view shows it. <paramref name="SkipReason" /> set means the file is
///     committed but has no lines in the index, so <paramref name="Content" /> is empty for a reason
///     the view can name.
/// </summary>
internal sealed record FileContentResponse(
    string QualifiedPath,
    string RepositorySlug,
    int LineCount,
    long SizeBytes,
    string? SkipReason,
    string Content,
    FileCommitResponse? FirstCommit,
    FileCommitResponse? LastCommit);

/// <summary>
///     The commit a file was first or last changed by, as the file view names it. Both are null where
///     no history was imported, which the view says rather than drawing an empty field: "no history"
///     and "never changed" are opposite claims (CONTEXT.md, History).
/// </summary>
internal sealed record FileCommitResponse(string Sha, string AuthorName, DateTimeOffset AuthoredAt, string Subject);

/// <summary>
///     One run of consecutive lines sharing an attribution, for the blame gutter.
///     <paramref name="By" /> is null for a run the build could not attribute.
/// </summary>
internal sealed record BlameRunResponse(int StartLine, int EndLine, FileCommitResponse? By);

/// <summary>
///     A file's blame. Its own response and its own request, because it is the one history read whose
///     size is the file's rather than a row's, and the file view draws the code without waiting for it.
/// </summary>
internal sealed record BlameResponse(string QualifiedPath, IReadOnlyList<BlameRunResponse> Runs);

/// <summary>
///     One name a file imports. <paramref name="TargetPath" /> and <paramref name="Unresolved" /> are
///     exclusive: the first is a qualified path the file route opens, the second the server's own
///     prose saying why there is no path to open. The raw <paramref name="Name" /> is there either
///     way, because an edge shown only when it resolves would read as a dependency the file does not
///     have (CONTEXT.md, Import).
/// </summary>
internal sealed record ImportEdgeResponse(string Name, int LineNumber, string? TargetPath, string? Unresolved);

/// <summary>
///     What a file imports, and what the server was able to say about the question at all.
///     <paramref name="Profiled" /> and <paramref name="HasImports" /> are the two ways an empty list
///     means something other than "this file imports nothing" — an extension no profile covers was
///     never read, and a language with no import concept has none to read — and the panel draws the
///     three apart. <paramref name="Capped" /> says the list stopped at the ceiling rather than at the
///     end of the file.
/// </summary>
internal sealed record FileImportsResponse(
    string QualifiedPath,
    string LanguageName,
    bool Profiled,
    bool HasImports,
    string? Module,
    bool Capped,
    IReadOnlyList<ImportEdgeResponse> Imports);

/// <summary>One file that imports the file asked about, and the line that does it.</summary>
internal sealed record DependentResponse(string QualifiedPath, string Name, int LineNumber);

/// <summary>
///     What imports a file. <paramref name="ShareTheModule" /> and <paramref name="Unplaced" /> are
///     why an empty list is not the sentence "nothing depends on this": a module several files declare
///     resolves to none of them, and an unresolved edge spelling this file's name may be a dependency
///     the index could not place. Both are zero where the question does not arise.
/// </summary>
internal sealed record FileDependentsResponse(
    string QualifiedPath,
    string? Module,
    int ShareTheModule,
    int Unplaced,
    bool Capped,
    IReadOnlyList<DependentResponse> Dependents);

/// <summary>One commit of the change log, with the message body and what it did to the tree in sums.</summary>
internal sealed record CommitResponse(
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
///     A page of the change log. <see cref="Total" /> counts every commit in scope, so the page can say
///     how many there are; zero is a project or repository without history, not an error.
/// </summary>
internal sealed record CommitListResponse(long Total, int Page, int PageSize, IReadOnlyList<CommitResponse> Commits);

/// <summary>One path a commit touched; <see cref="QualifiedPath" /> links to the file when it is still at HEAD.</summary>
internal sealed record CommitFileResponse(string Path, string ChangeKind, int Added, int Deleted, string? QualifiedPath);

internal sealed record CommitFilesResponse(string Sha, IReadOnlyList<CommitFileResponse> Files);

/// <summary>
///     One file of the churn ranking. <see cref="QualifiedPath" /> is how the project names the path
///     (ADR-0006) and is always set, because a window ranks paths that are no longer at HEAD and those
///     have to be named too; <see cref="AtHead" /> is what says whether there is a file there to open.
/// </summary>
internal sealed record ChurnFileResponse(
    string QualifiedPath,
    string RepositorySlug,
    bool AtHead,
    int Commits,
    long Added,
    long Deleted);

/// <summary>
///     A churn ranking and the window it covers. <see cref="Since" /> and <see cref="Until" /> are
///     null together when the scope holds no commit at all, which is a project or a repository without
///     imported history rather than an error.
///     The window ends at the newest recorded commit rather than today (CONTEXT.md, Window), so the
///     dates are part of the answer and not an echo of the request: a reader who asked for ninety days
///     and is shown a window ending two months ago has learned that the index is stale.
///     <see cref="WithoutHistory" /> names the repositories the ranking cannot speak for, so that a
///     ranking covering half a project is not read as covering all of it (CONTEXT.md, History). Empty
///     where the question does not arise: a project of one repository, or a scoped request.
/// </summary>
internal sealed record ChurnResponse(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    IReadOnlyList<ChurnFileResponse> Files,
    IReadOnlyList<string> WithoutHistory);

/// <summary>One file in a listing. The index's internal file id is deliberately not in it.</summary>
internal sealed record FileListEntry(
    string QualifiedPath,
    string RepositorySlug,
    int LineCount,
    long SizeBytes,
    string? SkipReason);

/// <summary>
///     A page of a listing. <paramref name="Total" /> counts every match and <paramref name="Files" />
///     the first <c>limit</c> of them, so the view can say how much it is not showing.
/// </summary>
internal sealed record FileListResponse(int Total, IReadOnlyList<FileListEntry> Files);

/// <summary>
///     One row of a tree listing. <paramref name="Files" /> is null for a file and counts everything
///     beneath for a directory, which is how the view tells them apart without a second field.
/// </summary>
internal sealed record TreeEntryResponse(
    string Name,
    string QualifiedPath,
    int? Files,
    long Lines,
    long SizeBytes,
    string? SkipReason);

/// <summary>
///     One level of the tree. <paramref name="Path" /> echoes the level that was asked for — empty at
///     the project root — so the view can draw a breadcrumb from the response alone.
///     <paramref name="RepositoryLevel" /> says whether the entries are repositories rather than
///     directories, which an empty path no longer implies: a single-repository project's root is
///     already inside its one repository (ADR-0006).
/// </summary>
internal sealed record TreeResponse(
    string Path,
    bool RepositoryLevel,
    IReadOnlyList<TreeEntryResponse> Entries);

/// <summary>
///     Browsing, searching and file reads for the operator UI. The same services answer the MCP tools;
///     what differs is the shape, because the browser renders the result itself where an agent is
///     handed prose.
/// </summary>
internal static class SearchEndpoints
{
    /// <summary>
    ///     A file view scrolls, so it asks for the whole file rather than a window. The index refuses
    ///     files over <c>Index:MaxFileBytes</c> (4 MiB by default), which bounds this well below it.
    /// </summary>
    private const int MaxLinesPerFileView = 100_000;

    public static void MapSearch(this RouteGroupBuilder api)
    {
        // The project is bound from the route (BoundProject), so an unknown slug is a 404 before the
        // index is asked; only a real project can answer "no index" below.
        var project = api.MapProject();

        // A project with no index answers with the explanation rather than an empty page of results:
        // "nothing matched" and "there is nothing to match against" mean opposite things. The search
        // route says it as a 400 like every other problem it has; the browsing routes below tell a 404
        // for no index from a 400 for a repository that does not exist, because the view draws them
        // differently.
        project.MapGet("/search", async (
                Project project, string q, GrepSearch search, CancellationToken ct,
                bool regex = false, bool caseSensitive = false, string? path = null,
                string? extension = null, int page = 1, int pageSize = 20) =>
            Answer(await search.SearchAsync(project.Slug,
                new GrepRequest(q, regex, caseSensitive, path, Extension: extension, Page: page,
                    PageSize: pageSize), ct)));

        project.MapGet("/files",
            async (Project project, ProjectIndexes indexes, CancellationToken ct, string glob = "*",
                    string? repository = null) =>
                await ListAsync(indexes, project.Slug, glob, repository, ct));

        project.MapGet("/tree",
            async (Project project, ProjectIndexes indexes, CancellationToken ct, string path = "") =>
                await TreeAsync(indexes, project.Slug, path, ct));

        project.MapGet("/file",
            async (Project project, string path, ProjectIndexes indexes, CancellationToken ct) =>
                await ReadAsync(indexes, project.Slug, path, ct));

        // Its own route rather than a flag on /file: the runs are the size of the file, and the view
        // renders the code without them and fills the gutter in when they arrive.
        project.MapGet("/file/blame",
            async (Project project, string path, ProjectIndexes indexes, CancellationToken ct) =>
                await BlameAsync(indexes, project.Slug, path, ct));

        // The two directions of the import graph, a route each rather than one that answers both:
        // they are two reads, the panels draw as each arrives, and a file page that had to wait for
        // the reverse lookup of a hub before showing what the file itself imports would be slower
        // than either answer is.
        project.MapGet("/file/imports",
            async (Project project, string path, ImportGraph graph, CancellationToken ct) =>
                Answer(await graph.ImportsAsync(project.Slug, path, ct)));

        project.MapGet("/file/dependents",
            async (Project project, string path, ImportGraph graph, CancellationToken ct) =>
                Answer(await graph.DependentsAsync(project.Slug, path, ct)));

        // The change log, paged. The files a commit touched are their own route, like blame is: a
        // page of fifty commits touching a few hundred paths each would be mostly paths nobody opens.
        project.MapGet("/commits",
            async (Project project, ProjectIndexes indexes, CancellationToken ct, string? repository = null,
                    int page = 1, int pageSize = DefaultCommitPage) =>
                await CommitsAsync(indexes, project.Slug, repository, page, pageSize, ct));

        // The churn ranking (CONTEXT.md, Churn), over a window of days rather than a page of commits:
        // it is a view of its own now, with nothing beside it to agree with. `days` and not a pair of
        // dates, because the window is anchored to the newest recorded commit and only the index knows
        // where that is — a client sending dates would be guessing at it.
        //
        // The route is named for the concept and the MCP tool is named `hot_files`, which is not an
        // oversight: a tool name is agent-facing and trades on the shell verbs a model already knows
        // (CODING_STANDARDS, Comments), where a URL the UI holds follows the vocabulary.
        project.MapGet("/churn",
            async (Project project, ProjectIndexes indexes, CancellationToken ct, string? repository = null,
                    int days = HistoryWindow.DefaultDays, int limit = ChurnFilesShown) =>
                await ChurnAsync(indexes, project.Slug, repository, days, limit, ct));

        project.MapGet("/commits/{sha}/files",
            async (Project project, string sha, ProjectIndexes indexes, CancellationToken ct) =>
                await CommitFilesAsync(indexes, project.Slug, sha, ct));
    }

    /// <summary>Commits per page of the change log when the caller does not say. A screen and a bit.</summary>
    private const int DefaultCommitPage = 50;

    /// <summary>The most commits one page may hold. The same ceiling <c>git_log</c> has.</summary>
    private const int MaxCommitPage = 200;

    private static Task<IResult> CommitsAsync(ProjectIndexes indexes, string project, string? repository,
        int page, int pageSize, CancellationToken cancellationToken) =>
        IndexReader.OverIndexAsync(indexes, project, repository, async (index, token) =>
        {
            pageSize = Math.Clamp(pageSize, 1, MaxCommitPage);
            page = Math.Max(1, page);
            string? scope = index.Repository?.Slug;
            long total = await index.CommitCountAsync(scope, token);
            var commits = await index.ChangeLogAsync(scope, pageSize, (page - 1) * pageSize, token);
            return Results.Ok(new CommitListResponse(total, page, pageSize,
                commits.Select(c => new CommitResponse(c.Sha, c.RepositorySlug, c.AuthorName, c.AuthorEmail,
                    c.AuthoredAt, c.Subject, c.Body, c.FilesChanged, c.Added, c.Deleted)).ToList()));
        }, Status, cancellationToken);

    /// <summary>
    ///     Files in a churn ranking when the caller does not say. A screenful: the ranking is read from
    ///     the top down, and its tail is noise.
    /// </summary>
    private const int ChurnFilesShown = 25;

    /// <summary>The ceiling <c>hot_files</c> has, for the same reason: past it a ranking is not read.</summary>
    private const int MaxChurnFiles = 100;

    /// <summary>
    ///     The most-changed files of a window. A scope with no commits at all answers with an empty
    ///     ranking and no dates rather than a failure: that is a project whose history has not been
    ///     imported, which the page says in its own words.
    /// </summary>
    private static Task<IResult> ChurnAsync(ProjectIndexes indexes, string project, string? repository,
        int days, int limit, CancellationToken cancellationToken) =>
        IndexReader.OverIndexAsync(indexes, project, repository, async (index, token) =>
        {
            string? scope = index.Repository?.Slug;
            // Read whether or not there is a ranking: an empty page is where a reader is most likely to
            // conclude that nothing changed. Which repositories these are is the reader's rule, not this
            // route's — the tool reply draws the same answer from the same place.
            var coverage = await index.HistoryCoverageAsync(scope, token);

            var window = await index.WindowAsync(days, scope, token);
            if (window is null) return Results.Ok(new ChurnResponse(null, null, [], coverage.Without));

            var ranked = await index.ChurnAsync(window, scope, null, Math.Clamp(limit, 1, MaxChurnFiles), token);
            return Results.Ok(new ChurnResponse(window.Since, window.Until,
                ranked.Select(f => new ChurnFileResponse(f.QualifiedPath, f.RepositorySlug, f.AtHead, f.Commits,
                    f.Added, f.Deleted)).ToList(), coverage.Without));
        }, Status, cancellationToken);

    private static Task<IResult> CommitFilesAsync(ProjectIndexes indexes, string project, string sha,
        CancellationToken cancellationToken) =>
        IndexReader.OverIndexAsync(indexes, project, null, async (index, token) =>
        {
            var files = await index.CommitFilesAsync(sha, token);
            // No files means no such commit, in practice: every commit the walk records touched something,
            // the root included. Said as a 404 rather than an empty list the page would draw as "nothing".
            if (files.Count == 0)
                return Results.NotFound(new { error = $"No commit '{sha}' in the history of project '{project}'." });
            return Results.Ok(new CommitFilesResponse(sha,
                files.Select(f => new CommitFileResponse(f.Path, f.ChangeKind, f.Added, f.Deleted, f.QualifiedPath))
                    .ToList()));
        }, Status, cancellationToken);

    private static Task<IResult> ListAsync(
        ProjectIndexes indexes, string project, string glob, string? repository,
        CancellationToken cancellationToken) =>
        IndexReader.OverIndexAsync(indexes, project, repository, async (index, token) =>
        {
            var result = await index.GlobAsync(glob, IndexReader.MaxFiles, token);
            return Results.Ok(new FileListResponse(result.Total,
                result.Files
                    .Select(f => new FileListEntry(f.QualifiedPath, f.RepositorySlug, f.LineCount, f.SizeBytes,
                        f.SkipReason))
                    .ToList()));
        }, Status, cancellationToken);

    /// <summary>
    ///     A level of the tree. An unknown directory answers with an empty level rather than an error:
    ///     the view has a breadcrumb out of it. A path whose first segment names no repository is a
    ///     400 with the repositories that exist, the same answer every other reader gives that slug — a
    ///     stale link is told what changed rather than shown an empty tree.
    /// </summary>
    private static Task<IResult> TreeAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken) =>
        IndexReader.OverDirectoryAsync(indexes, project, path, async (index, directory, token) =>
        {
            // The project level is the list of repositories, which a single-repository project does
            // not have: there it is that repository's own top level (ADR-0006).
            var paths = await index.PathsAsync(token);
            var location = directory.Repository is null
                ? paths.SingleRepository ? new QualifiedPath(paths.RepositorySlug, "") : null
                : new QualifiedPath(directory.Repository.Slug, directory.PathInRepository);

            var entries = await index.TreeAsync(location, 1, token);
            return Results.Ok(new TreeResponse(directory.QualifiedPath, location is null,
                entries
                    .Select(e => new TreeEntryResponse(e.Name, e.QualifiedPath, e.Files, e.Lines, e.SizeBytes,
                        e.SkipReason))
                    .ToList()));
        }, Status, cancellationToken);

    private static Task<IResult> ReadAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken) =>
        IndexReader.OverIndexAsync(indexes, project, null, async (index, token) =>
        {
            if (await index.FindFileAsync(path, token) is not { } file)
                return Results.NotFound(new { error = $"No file '{path}' in project '{project}'." });

            var lines = file.SkipReason is null
                ? await index.LinesAsync(file.FileId, 1, MaxLinesPerFileView, token)
                : [];
            // Carried on the file read and not fetched separately: they are columns on the row that read
            // already has in hand, so a second request would be one for data this one was holding.
            var span = await index.FileCommitsAsync(file.FileId, token);
            return Results.Ok(new FileContentResponse(file.QualifiedPath, file.RepositorySlug, file.LineCount,
                file.SizeBytes, file.SkipReason, string.Join('\n', lines), Commit(span.First), Commit(span.Last)));
        }, Status, cancellationToken);

    /// <summary>
    ///     A file's attribution as runs. A file with no history answers with no runs rather than a
    ///     failure: the gutter simply does not draw, and the header line is what says why.
    /// </summary>
    private static Task<IResult> BlameAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken) =>
        IndexReader.OverIndexAsync(indexes, project, null, async (index, token) =>
        {
            if (await index.FindFileAsync(path, token) is not { } file)
                return Results.NotFound(new { error = $"No file '{path}' in project '{project}'." });
            if (file.SkipReason is not null) return Results.Ok(new BlameResponse(file.QualifiedPath, []));

            var runs = await index.BlameAsync(file.FileId, 1, MaxLinesPerFileView, token);
            return Results.Ok(new BlameResponse(file.QualifiedPath,
                runs.Select(r => new BlameRunResponse(r.StartLine, r.EndLine, Commit(r.By))).ToList()));
        }, Status, cancellationToken);

    /// <summary>
    ///     A search outcome as JSON. A problem is a 400 whichever kind it is, where the browsing routes
    ///     split 404 from 400: these routes are reached with a file already open or a query already
    ///     typed, so a refusal is a page gone stale rather than a state the view draws differently, and
    ///     the prose is the whole of what it shows. Written once because three routes answer with the
    ///     same outcome type, and three copies of the mapping would agree only until one was edited.
    /// </summary>
    private static IResult Answer(Outcome outcome) => outcome switch
    {
        GrepResult result => Results.Ok(result),
        ImportsResult result => Results.Ok(new FileImportsResponse(result.QualifiedPath, result.LanguageName,
            result.Profiled, result.HasImports, result.Module, result.Capped,
            result.Imports
                .Select(i => new ImportEdgeResponse(i.Name, i.LineNumber, i.TargetPath, i.Unresolved))
                .ToList())),
        DependentsResult result => Results.Ok(new FileDependentsResponse(result.QualifiedPath, result.Module,
            result.ShareTheModule, result.Unplaced, result.Capped,
            result.Dependents
                .Select(d => new DependentResponse(d.QualifiedPath, d.Name, d.LineNumber))
                .ToList())),
        Problem problem => Status(problem),
        // Unreachable while these are the outcomes grep and the graph answer with, and a 500 rather
        // than a cast that throws if another is ever added.
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };

    private static FileCommitResponse? Commit(AttributedBy? by) =>
        by is null ? null : new FileCommitResponse(by.Sha, by.AuthorName, by.AuthoredAt, by.Subject);

    /// <summary>
    ///     No index is a 404 — the view renders it as the starting state a new project is in — and every
    ///     other problem a 400, because the project is there and the request asked it something wrong.
    /// </summary>
    private static IResult Status(Problem problem) => problem.Kind == ProblemKind.NoIndex
        ? Results.NotFound(new { error = problem.Explanation })
        : Results.BadRequest(new { error = problem.Explanation });
}
