namespace CodeExplorer;

/// <summary>
///     The two endpoints a refresh is driven by: one that asks for it and one that reports on it.
///     They are separate because the work outlives the request — a Container Apps Job on a cron fires
///     the first and never looks again, while the web UI polls the second (ADR-0004). Authentication
///     is off until the ticket that adds it, so nothing here checks a caller yet.
/// </summary>
internal static class RefreshEndpoints
{
    public static void MapRefresh(this RouteGroupBuilder api)
    {
        api.MapPost("/projects/{project}/refresh",
            async (string project, ControlDatabase control, RefreshService refreshes, CancellationToken ct) =>
                await control.FindAsync(project, ct) is { } found
                    ? refreshes.Request(found) switch
                    {
                        // Accepted, not OK: the shadow build has not started yet, and the Location header
                        // is where the caller watches it.
                        { Refused: null, Status: var status } => Results.Accepted(
                            $"/api/projects/{project}/refresh", status),
                        { Refused: var refused } => Results.Json(new { error = refused.Message },
                            statusCode: refused.StatusCode)
                    }
                    : Results.NotFound(new { error = $"No project with slug '{project}'." }));

        api.MapGet("/projects/{project}/refresh",
            async (string project, ControlDatabase control, RefreshService refreshes, CancellationToken ct) =>
                // A status exists only for a project that existed when it was asked for, so the common
                // case — a page polling this once a second while a rebuild runs — answers from memory.
                // The control database is read only to tell an unknown slug from one that never refreshed.
                refreshes.Find(project) is { } status
                    ? Results.Ok(status)
                    : await control.FindAsync(project, ct) is null
                        ? Results.NotFound(new { error = $"No project with slug '{project}'." })
                        : Results.Ok(refreshes.Status(project)));
    }
}
