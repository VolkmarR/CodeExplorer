namespace CodeExplorer;

/// <summary>
///     What the operator UI reads and the two things it removes. The creating endpoints are in
///     <c>Control/</c>, which is all they touch; these span modules, as <see cref="ProjectOverview" />
///     explains.
/// </summary>
internal static class OperatorEndpoints
{
    public static void MapOperator(this RouteGroupBuilder api)
    {
        api.MapGet("/projects",
            (ProjectOverview overview, CancellationToken ct) => overview.ListAsync(ct));

        api.MapGet("/projects/{project}", async (string project, ProjectOverview overview, CancellationToken ct) =>
            await overview.FindAsync(project, ct) is { } detail
                ? Results.Ok(detail)
                : Results.NotFound(new { error = $"No project with slug '{project}'." }));

        api.MapDelete("/projects/{project}", async (string project, ProjectOverview overview, CancellationToken ct) =>
            await overview.DeleteAsync(project, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"No project with slug '{project}'." }));

        api.MapDelete("/projects/{project}/repositories/{repository}",
            async (string project, string repository, ProjectOverview overview, CancellationToken ct) =>
                await overview.DeleteRepositoryAsync(project, repository, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new
                        { error = $"Project '{project}' has no repository with slug '{repository}'." }));
    }
}
