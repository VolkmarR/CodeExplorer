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
/// </summary>
internal sealed record TreeResponse(string Path, IReadOnlyList<TreeEntryResponse> Entries);

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

    /// <summary>
    ///     Enough to browse a repository's whole source tree in one page. Past it the listing is not a
    ///     thing to read, and the total in the response says so.
    /// </summary>
    private const int MaxFilesListed = 2000;

    public static void MapSearch(this RouteGroupBuilder api)
    {
        // A project with no index answers with the explanation rather than an empty page of results:
        // "nothing matched" and "there is nothing to match against" mean opposite things.
        api.MapGet("/projects/{project}/search", async (
                string project, string q, GrepSearch search, CancellationToken ct,
                bool regex = false, bool caseSensitive = false, string? path = null,
                string? extension = null, int page = 1, int pageSize = 20) =>
            await search.SearchAsync(project,
                new GrepRequest(q, regex, caseSensitive, path, Extension: extension, Page: page,
                    PageSize: pageSize), ct) switch
            {
                GrepResult result => Results.Ok(result),
                GrepProblem problem => Results.BadRequest(new { error = problem.Explanation }),
                // Unreachable while GrepOutcome has two cases, and a 500 rather than a cast that
                // throws if a third is ever added.
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            });

        api.MapGet("/projects/{project}/files",
            async (string project, ProjectIndexes indexes, CancellationToken ct, string glob = "*",
                    string? repository = null) =>
                await ListAsync(indexes, project, glob, repository, ct));

        api.MapGet("/projects/{project}/tree",
            async (string project, ProjectIndexes indexes, CancellationToken ct, string path = "") =>
                await TreeAsync(indexes, project, path, ct));

        api.MapGet("/projects/{project}/file",
            async (string project, string path, ProjectIndexes indexes, CancellationToken ct) =>
                await ReadAsync(indexes, project, path, ct));
    }

    private static async Task<IResult> ListAsync(
        ProjectIndexes indexes, string project, string glob, string? repository,
        CancellationToken cancellationToken)
    {
        using var index = await FileQueries.OpenAsync(indexes, project, cancellationToken);
        if (index is null) return Results.NotFound(new { error = ToolReply.NoIndex(project) });

        var result = await index.GlobAsync(glob, repository, MaxFilesListed, cancellationToken);
        return Results.Ok(new FileListResponse(result.Total,
            result.Files
                .Select(f => new FileListEntry(f.QualifiedPath, f.RepositorySlug, f.LineCount, f.SizeBytes,
                    f.SkipReason))
                .ToList()));
    }

    /// <summary>
    ///     A level of the tree. An unknown repository or directory answers with an empty level rather
    ///     than a 404: the view has a breadcrumb out of it, and the only 404 here means the project has
    ///     no index at all, which is a different thing to say.
    /// </summary>
    private static async Task<IResult> TreeAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken)
    {
        using var index = await FileQueries.OpenAsync(indexes, project, cancellationToken);
        if (index is null) return Results.NotFound(new { error = ToolReply.NoIndex(project) });

        var location = QualifiedPath.Parse(path);
        var entries = await index.TreeAsync(path, cancellationToken);
        return Results.Ok(new TreeResponse(location?.ToString() ?? "",
            entries
                .Select(e => new TreeEntryResponse(e.Name, e.QualifiedPath, e.Files, e.Lines, e.SizeBytes,
                    e.SkipReason))
                .ToList()));
    }

    private static async Task<IResult> ReadAsync(
        ProjectIndexes indexes, string project, string path, CancellationToken cancellationToken)
    {
        using var index = await FileQueries.OpenAsync(indexes, project, cancellationToken);
        if (index is null) return Results.NotFound(new { error = ToolReply.NoIndex(project) });

        if (await index.FindFileAsync(path, cancellationToken) is not { } file)
            return Results.NotFound(new { error = $"No file '{path}' in project '{project}'." });

        var lines = file.SkipReason is null
            ? await index.LinesAsync(file.FileId, 1, MaxLinesPerFileView, cancellationToken)
            : [];
        return Results.Ok(new FileContentResponse(file.QualifiedPath, file.RepositorySlug, file.LineCount,
            file.SizeBytes, file.SkipReason, string.Join('\n', lines)));
    }
}
