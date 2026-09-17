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
internal sealed record HotFileResponse(
    string QualifiedPath,
    string RepositorySlug,
    bool AtHead,
    int Commits,
    long Added,
    long Deleted);

/// <summary>
///     The churn ranking for the span a page of the change log covers. <see cref="Since" /> and
///     <see cref="Until" /> are null together when the page holds no commit, which is a project or a
///     repository without imported history rather than an error.
///     <see cref="WithoutHistory" /> names the repositories the ranking cannot speak for, so that a
///     ranking covering half a project is not read as covering all of it (CONTEXT.md, History). Empty
///     where the question does not arise: a project of one repository, or a scoped request.
/// </summary>
internal sealed record HotFilesResponse(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    IReadOnlyList<HotFileResponse> Files,
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
            await search.SearchAsync(project.Slug,
                new GrepRequest(q, regex, caseSensitive, path, Extension: extension, Page: page,
                    PageSize: pageSize), ct) switch
            {
                GrepResult result => Results.Ok(result),
                SearchProblem problem => Results.BadRequest(new { error = problem.Explanation }),
                // Unreachable while grep answers with two outcome cases, and a 500 rather than a cast that
                // throws if a third is ever added.
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            });

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

        // The change log, paged. The files a commit touched are their own route, like blame is: a
        // page of fifty commits touching a few hundred paths each would be mostly paths nobody opens.
        project.MapGet("/commits",
            async (Project project, ProjectIndexes indexes, CancellationToken ct, string? repository = null,
                    int page = 1, int pageSize = DefaultCommitPage) =>
                await CommitsAsync(indexes, project.Slug, repository, page, pageSize, ct));

        // The churn ranking for the window the change log's page is showing. It takes the page
        // arguments rather than dates so that a caller asking both routes the same thing gets a
        // ranking and a list describing the same commits; only the page decides the window.
        project.MapGet("/hot-files",
            async (Project project, ProjectIndexes indexes, CancellationToken ct, string? repository = null,
                    int page = 1, int pageSize = DefaultCommitPage) =>
                await HotFilesAsync(indexes, project.Slug, repository, page, pageSize, ct));

        project.MapGet("/commits/{sha}/files",
            async (Project project, string sha, ProjectIndexes indexes, CancellationToken ct) =>
                await CommitFilesAsync(indexes, project.Slug, sha, ct));
    }

    /// <summary>Commits per page of the change log when the caller does not say. A screen and a bit.</summary>
    private const int DefaultCommitPage = 50;

    /// <summary>The most commits one page may hold. The same ceiling <c>git_log</c> has.</summary>
    private const int MaxCommitPage = 200;

    private static async Task<IResult> CommitsAsync(ProjectIndexes indexes, string project, string? repository,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project, repository, cancellationToken);
        if (open is IndexOpen.Refused refused) return Refuse(refused);
        using var index = ((IndexOpen.Opened)open).Reader;

        pageSize = Math.Clamp(pageSize, 1, MaxCommitPage);
        page = Math.Max(1, page);
        string? scope = index.Repository?.Slug;
        long total = await index.CommitCountAsync(scope, cancellationToken);
        var commits = await index.ChangeLogAsync(scope, pageSize, (page - 1) * pageSize, cancellationToken);
        return Results.Ok(new CommitListResponse(total, page, pageSize,
            commits.Select(c => new CommitResponse(c.Sha, c.RepositorySlug, c.AuthorName, c.AuthorEmail,
                c.AuthoredAt, c.Subject, c.Body, c.FilesChanged, c.Added, c.Deleted)).ToList()));
    }

    /// <summary>
    ///     Files in one churn panel. A panel beside a page of commits and not a page of its own, so it
    ///     is as long as a reader glances at rather than as long as the ranking goes. Fixed rather than
    ///     a parameter: nothing on the page offers to change it, and an unused knob on a route is one
    ///     nothing proves the behaviour of.
    /// </summary>
    private const int HotFilesShown = 10;

    /// <summary>
    ///     The most-changed files of the span one page of the change log covers. A page with no
    ///     commits answers with an empty ranking and no dates rather than a failure: that is a project
    ///     whose history has not been imported, which the page already says in its own words.
    /// </summary>
    private static async Task<IResult> HotFilesAsync(ProjectIndexes indexes, string project, string? repository,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project, repository, cancellationToken);
        if (open is IndexOpen.Refused refused) return Refuse(refused);
        using var index = ((IndexOpen.Opened)open).Reader;

        pageSize = Math.Clamp(pageSize, 1, MaxCommitPage);
        page = Math.Max(1, page);
        string? scope = index.Repository?.Slug;
        // Read whether or not there is a ranking: the empty panel is where a reader is most likely to
        // conclude that nothing changed. Which repositories these are is the reader's rule, not this
        // route's — the tool reply draws the same answer from the same place.
        var coverage = await index.HistoryCoverageAsync(scope, cancellationToken);

        var window = await index.CommitPageWindowAsync(scope, pageSize, (page - 1) * pageSize, cancellationToken);
        if (window is null) return Results.Ok(new HotFilesResponse(null, null, [], coverage.Without));

        var ranked = await index.ChurnAsync(window, scope, null, HotFilesShown, cancellationToken);
        return Results.Ok(new HotFilesResponse(window.Since, window.Until,
            ranked.Select(f => new HotFileResponse(f.QualifiedPath, f.RepositorySlug, f.AtHead, f.Commits, f.Added,
                f.Deleted)).ToList(), coverage.Without));
    }

    private static async Task<IResult> CommitFilesAsync(ProjectIndexes indexes, string project, string sha,
        CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project, null, cancellationToken);
        if (open is IndexOpen.Refused refused) return Refuse(refused);
        using var index = ((IndexOpen.Opened)open).Reader;

        var files = await index.CommitFilesAsync(sha, cancellationToken);
        // No files means no such commit, in practice: every commit the walk records touched something,
        // the root included. Said as a 404 rather than an empty list the page would draw as "nothing".
        if (files.Count == 0)
            return Results.NotFound(new { error = $"No commit '{sha}' in the history of project '{project}'." });
        return Results.Ok(new CommitFilesResponse(sha,
            files.Select(f => new CommitFileResponse(f.Path, f.ChangeKind, f.Added, f.Deleted, f.QualifiedPath))
                .ToList()));
    }

    private static async Task<IResult> ListAsync(
        ProjectIndexes indexes, string project, string glob, string? repository,
        CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project, repository, cancellationToken);
        if (open is IndexOpen.Refused refused) return Refuse(refused);
        using var index = ((IndexOpen.Opened)open).Reader;

        var result = await index.GlobAsync(glob, IndexReader.MaxFiles, cancellationToken);
        return Results.Ok(new FileListResponse(result.Total,
            result.Files
                .Select(f => new FileListEntry(f.QualifiedPath, f.RepositorySlug, f.LineCount, f.SizeBytes,
                    f.SkipReason))
                .ToList()));
    }

    /// <summary>
    ///     A level of the tree. An unknown directory answers with an empty level rather than an error:
    ///     the view has a breadcrumb out of it. A path whose first segment names no repository is a
    ///     400 with the repositories that exist, the same answer every other reader gives that slug — a
    ///     stale link is told what changed rather than shown an empty tree.
    /// </summary>
    private static async Task<IResult> TreeAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project, null, cancellationToken);
        if (open is IndexOpen.Refused refused) return Refuse(refused);
        using var index = ((IndexOpen.Opened)open).Reader;

        // The shape has to be known before the path can be read at all: `src/x.ts` is a file in one
        // project and a repository in another (ADR-0006). Null back from Parse is the repository level,
        // which a single-repository project does not have.
        var paths = await index.PathsAsync(cancellationToken);
        var location = paths.Parse(path);
        if (location is not null)
        {
            if (await index.FindRepositoryAsync(location.RepositorySlug, cancellationToken) is not { } repository)
                return Results.BadRequest(new
                    { error = await index.UnknownRepositoryAsync(location.RepositorySlug, cancellationToken) });
            location = location with { RepositorySlug = repository.Slug };
        }

        var entries = await index.TreeAsync(location, 1, cancellationToken);
        return Results.Ok(new TreeResponse(location is null ? "" : paths.Format(location), location is null,
            entries
                .Select(e => new TreeEntryResponse(e.Name, e.QualifiedPath, e.Files, e.Lines, e.SizeBytes,
                    e.SkipReason))
                .ToList()));
    }

    private static async Task<IResult> ReadAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project, null, cancellationToken);
        if (open is IndexOpen.Refused refused) return Refuse(refused);
        using var index = ((IndexOpen.Opened)open).Reader;

        if (await index.FindFileAsync(path, cancellationToken) is not { } file)
            return Results.NotFound(new { error = $"No file '{path}' in project '{project}'." });

        var lines = file.SkipReason is null
            ? await index.LinesAsync(file.FileId, 1, MaxLinesPerFileView, cancellationToken)
            : [];
        // Carried on the file read and not fetched separately: they are columns on the row that read
        // already has in hand, so a second request would be one for data this one was holding.
        var span = await index.FileCommitsAsync(file.FileId, cancellationToken);
        return Results.Ok(new FileContentResponse(file.QualifiedPath, file.RepositorySlug, file.LineCount,
            file.SizeBytes, file.SkipReason, string.Join('\n', lines), Commit(span.First), Commit(span.Last)));
    }

    /// <summary>
    ///     A file's attribution as runs. A file with no history answers with no runs rather than a
    ///     failure: the gutter simply does not draw, and the header line is what says why.
    /// </summary>
    private static async Task<IResult> BlameAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project, null, cancellationToken);
        if (open is IndexOpen.Refused refused) return Refuse(refused);
        using var index = ((IndexOpen.Opened)open).Reader;

        if (await index.FindFileAsync(path, cancellationToken) is not { } file)
            return Results.NotFound(new { error = $"No file '{path}' in project '{project}'." });
        if (file.SkipReason is not null) return Results.Ok(new BlameResponse(file.QualifiedPath, []));

        var runs = await index.BlameAsync(file.FileId, 1, MaxLinesPerFileView, cancellationToken);
        return Results.Ok(new BlameResponse(file.QualifiedPath,
            runs.Select(r => new BlameRunResponse(r.StartLine, r.EndLine, Commit(r.By))).ToList()));
    }

    private static FileCommitResponse? Commit(AttributedBy? by) =>
        by is null ? null : new FileCommitResponse(by.Sha, by.AuthorName, by.AuthoredAt, by.Subject);

    /// <summary>
    ///     No index is a 404 — the view renders it as the starting state a new project is in — and an
    ///     unknown repository a 400, because the project is there and the request named something in it
    ///     that is not.
    /// </summary>
    private static IResult Refuse(IndexOpen.Refused refused) => refused switch
    {
        IndexOpen.NoIndex => Results.NotFound(new { error = refused.Explanation }),
        IndexOpen.UnknownRepository => Results.BadRequest(new { error = refused.Explanation }),
        // Unreachable while Refused has two cases, and a 500 rather than a cast that throws if a
        // third is ever added.
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
}
