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
    string Content);

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
                GrepProblem problem => Results.BadRequest(new { error = problem.Explanation }),
                // Unreachable while GrepOutcome has two cases, and a 500 rather than a cast that
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
        return Results.Ok(new FileContentResponse(file.QualifiedPath, file.RepositorySlug, file.LineCount,
            file.SizeBytes, file.SkipReason, string.Join('\n', lines)));
    }

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
