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
                        { Accepted: true, Status: var status } => Results.Accepted(
                            $"/api/projects/{project}/refresh", status),
                        // 507 and not another 409, so a cron can tell "someone else is rebuilding, try
                        // later" from "this replica is out of disk", which need different responses.
                        { Refusal: var refusal, OutOfDisk: true } => Results.Json(new { error = refusal },
                            statusCode: StatusCodes.Status507InsufficientStorage),
                        var refused => Results.Conflict(new { error = refused.Refusal })
                    }
                    : Results.NotFound(new { error = $"No project with slug '{project}'." }));

        api.MapGet("/projects/{project}/refresh",
            async (string project, ControlDatabase control, RefreshService refreshes, CancellationToken ct) =>
                await control.FindAsync(project, ct) is null
                    ? Results.NotFound(new { error = $"No project with slug '{project}'." })
                    : Results.Ok(refreshes.Status(project)));
    }
}
