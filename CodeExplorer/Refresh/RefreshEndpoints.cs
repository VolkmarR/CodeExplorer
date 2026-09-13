namespace CodeExplorer;

/// <summary>
///     The two endpoints a refresh is driven by, and the warm-up beside them (ADR-0005). The first two
///     are separate because the work outlives the request — a Container Apps Job on a cron fires the
///     one and never looks again, while the web UI polls the other (ADR-0004). Authentication is off
///     until the ticket that adds it, so nothing here checks a caller yet.
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

        // POST because it does work rather than reports it, and no UI calls it: an external cron fires
        // this before working hours so the first agent of the morning does not wait for a restore (#9).
        // It answers when the last project is warm, which is what a cron waits on; a refresh cannot be
        // answered that way and is the endpoint above with a status to poll.
        api.MapPost("/warmup", (WarmUp warmUp, CancellationToken ct) => warmUp.RunAsync(ct));
    }
}
