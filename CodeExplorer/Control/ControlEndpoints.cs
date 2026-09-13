namespace CodeExplorer;

internal sealed record CreateProjectRequest(string Slug, string? Name);

internal sealed record AddRepositoryRequest(string Slug, string Url, string? Credential);

/// <summary>What the API says about a repository. The credential itself is deliberately absent.</summary>
internal sealed record RepositoryResponse(string Slug, string Url, bool HasCredential);

/// <summary>
///     Operator endpoints for projects and repositories. Authentication is off until the ticket that
///     adds it (ADR-0004: absent configuration means an unauthenticated server).
/// </summary>
internal static class ControlEndpoints
{
    public static void MapControl(this RouteGroupBuilder api)
    {
        api.MapPost("/projects", async (CreateProjectRequest request, ControlDatabase control, CancellationToken ct) =>
            await control.CreateAsync(request.Slug, request.Name, ct) switch
            {
                CreateProjectOutcome.Created => Results.Created($"/projects/{request.Slug}/mcp",
                    new Project(request.Slug, request.Name!.Trim())),
                CreateProjectOutcome.InvalidSlug => Results.BadRequest(new { error = ControlDatabase.SlugRule }),
                CreateProjectOutcome.MissingName => Results.BadRequest(new { error = "Name is required." }),
                _ => Results.Conflict(new { error = $"A project with slug '{request.Slug}' already exists." })
            });

        // The credential is accepted here and nowhere else surfaces it: responses carry only whether one is set.
        api.MapPost("/projects/{project}/repositories",
            async (string project, AddRepositoryRequest request, ControlDatabase control, CancellationToken ct) =>
                await control.AddRepositoryAsync(project, request.Slug, request.Url, request.Credential, ct) switch
                {
                    (AddRepositoryOutcome.Created, { } added) => Results.Created(
                        $"/api/projects/{project}/repositories",
                        new RepositoryResponse(added.Slug, added.Url, added.HasCredential)),
                    (AddRepositoryOutcome.NoProject, _) => Results.NotFound(new
                        { error = $"No project with slug '{project}'." }),
                    (AddRepositoryOutcome.InvalidSlug, _) => Results.BadRequest(
                        new { error = ControlDatabase.SlugRule }),
                    (AddRepositoryOutcome.InvalidUrl, _) => Results.BadRequest(new { error = RepositoryUrl.Rule }),
                    _ => Results.Conflict(new
                        { error = $"Project '{project}' already has a repository with slug '{request.Slug}'." })
                });
        api.MapGet("/projects/{project}/repositories",
            async (string project, ControlDatabase control, CancellationToken ct) =>
                await control.FindAsync(project, ct) is null
                    ? Results.NotFound(new { error = $"No project with slug '{project}'." })
                    : Results.Ok((await control.ListRepositoriesAsync(project, ct))
                        .Select(r => new RepositoryResponse(r.Slug, r.Url, r.HasCredential))));
    }
}
