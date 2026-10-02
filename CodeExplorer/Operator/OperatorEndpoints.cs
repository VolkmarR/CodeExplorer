using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;

namespace CodeExplorer.Operator;

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

        // In memory and since this replica started, so neither reads an index or the control database.
        api.MapGet("/tool-calls", (ToolStatistics statistics) => statistics.Activity());

        // The project is bound from the route (BoundProject): an unknown slug never reaches these.
        var project = api.MapProject();

        project.MapGet("/tool-calls", (Project project, ToolStatistics statistics) => statistics.For(project.Slug));

        project.MapGet("", (Project project, ProjectOverview overview, CancellationToken ct) =>
            overview.FindAsync(project, ct));

        // The page's filters are the query string, the way its URL carries them (#216).
        project.MapGet("/overview", (Project project, int? days, string? repository, bool? showExcluded,
                ProjectOverview overview, CancellationToken ct) =>
            overview.OverviewAsync(project,
                new OverviewFilter(days ?? HistoryWindow.DefaultDays, repository, showExcluded ?? false), ct));

        // Proposals only (#217): the settings form adds the kept ones to its draft and a save is the PUT.
        project.MapGet("/excluded-paths/suggestions", (Project project, ProjectOverview overview,
            CancellationToken ct) => overview.SuggestExcludedPathsAsync(project, ct));

        project.MapDelete("", async (Project project, ProjectOverview overview, CancellationToken ct) =>
            await overview.DeleteAsync(project, ct) is { } refused
                // The refusal carries its own status code, as a refused refresh does.
                ? Results.Json(new { error = refused.Message }, statusCode: refused.StatusCode)
                : Results.NoContent());

        project.MapDelete("/repositories/{repository}",
            async (Project project, string repository, ProjectOverview overview, CancellationToken ct) =>
                await overview.DeleteRepositoryAsync(project, repository, ct) switch
                {
                    // The refusal carries its own status code, as a refused refresh does.
                    { Refused: { } refused } => Results.Json(new { error = refused.Message },
                        statusCode: refused.StatusCode),
                    { Found: true } => Results.NoContent(),
                    _ => Results.NotFound(new
                        { error = $"Project '{project.Slug}' has no repository with slug '{repository}'." })
                });
    }
}
