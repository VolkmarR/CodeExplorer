namespace CodeExplorer;

/// <summary>Operator endpoint that builds a project's index.</summary>
internal static class IndexEndpoints
{
    public static void MapIndex(this RouteGroupBuilder api) =>
        // Builds in the request. #8 replaces this with a shadow build and swap driven by the refresh
        // endpoints; until then a rebuild takes the project offline while it runs.
        api.MapPost("/projects/{project}/index",
            async (string project, ControlDatabase control, IndexBuilder indexer, CancellationToken ct) =>
                await control.FindAsync(project, ct) is { } found
                    ? Results.Ok(await indexer.BuildAsync(found, ct))
                    : Results.NotFound(new { error = $"No project with slug '{project}'." }));
}
