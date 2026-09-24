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

        // The project is bound from the route (BoundProject): an unknown slug never reaches these.
        var project = api.MapProject();

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

        project.MapDelete("", (Project project, ProjectOverview overview, CancellationToken ct) =>
            NoContentAfter(overview.DeleteAsync(project, ct)));

        project.MapDelete("/repositories/{repository}",
            async (Project project, string repository, ProjectOverview overview, CancellationToken ct) =>
                await overview.DeleteRepositoryAsync(project, repository, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new
                        { error = $"Project '{project.Slug}' has no repository with slug '{repository}'." }));
    }

    /// <summary>A removal that cannot fail once the project is bound answers 204, which a Task alone would not.</summary>
    private static async Task<IResult> NoContentAfter(Task removal)
    {
        await removal;
        return Results.NoContent();
    }
}
