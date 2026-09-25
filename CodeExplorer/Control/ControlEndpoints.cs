using CodeExplorer.Infrastructure;

namespace CodeExplorer.Control;

/// <summary>
///     <paramref name="SingleRepository" /> is absent on an older client's request and defaults to the
///     shape every project had before ADR-0006. It is accepted here and in no update, because it
///     cannot change once the project exists.
/// </summary>
internal sealed record CreateProjectRequest(string? Slug, string? Name, bool SingleRepository = false);

internal sealed record AddRepositoryRequest(string? Slug, string Url, string? Credential);

/// <summary>
///     A project's excluded paths, both ways: the whole list is read and the whole list is written, so
///     a save is one list the operator can see in the form and never a patch against one they cannot.
///     A PUT answers with the list as stored, normalised, so the form shows what was kept.
/// </summary>
internal sealed record ExcludedPathsBody(IReadOnlyList<string>? Patterns);

/// <summary>What the API says about a repository. The credential itself is deliberately absent.</summary>
internal sealed record RepositoryResponse(string Slug, string Url, bool HasCredential);

/// <summary>
///     Operator endpoints for projects and repositories. Nothing here says anything about who may
///     call it: the fallback policy <see cref="Authentication" /> installs requires an authenticated
///     caller of every endpoint that does not opt out, and where no tenant is configured there is no
///     policy and the server is open by design (ADR-0004).
/// </summary>
internal static class ControlEndpoints
{
    public static void MapControl(this RouteGroupBuilder api)
    {
        api.MapPost("/projects", async (CreateProjectRequest request, ControlDatabase control, CancellationToken ct) =>
            await control.CreateAsync(request.Slug, request.Name, request.SingleRepository, ct) switch
            {
                CreateProjectOutcome.Created => Results.Created($"/projects/{request.Slug}/mcp",
                    new Project(request.Slug!, request.Name!.Trim(), request.SingleRepository)),
                CreateProjectOutcome.InvalidSlug => Results.BadRequest(new { error = ControlDatabase.SlugRule }),
                CreateProjectOutcome.MissingName => Results.BadRequest(new { error = "Name is required." }),
                _ => Results.Conflict(new { error = $"A project with slug '{request.Slug}' already exists." })
            });

        // The project is bound from the route (BoundProject): an unknown slug never reaches these.
        var project = api.MapProject();

        // The credential is accepted here and nowhere else surfaces it: responses carry only whether one is set.
        project.MapPost("/repositories",
            async (Project project, AddRepositoryRequest request, ControlDatabase control, CancellationToken ct) =>
                await control.AddRepositoryAsync(project.Slug, request.Slug, request.Url, request.Credential, ct) switch
                {
                    (AddRepositoryOutcome.Created, { } added) => Results.Created(
                        $"/api/projects/{project.Slug}/repositories",
                        new RepositoryResponse(added.Slug, added.Url, added.HasCredential)),
                    // Bound a moment ago and gone now: an operator deleted it between the two reads.
                    (AddRepositoryOutcome.NoProject, _) => Results.NotFound(new
                        { error = BoundProject.NotFound(project.Slug) }),
                    (AddRepositoryOutcome.InvalidSlug, _) => Results.BadRequest(
                        new { error = ControlDatabase.SlugRule }),
                    (AddRepositoryOutcome.InvalidUrl, _) => Results.BadRequest(new { error = RepositoryUrl.Rule }),
                    (AddRepositoryOutcome.LocalNotAllowed, _) => Results.BadRequest(new
                        { error = $"The repository was not added. {RepositoryUrl.LocalRefusal}" }),
                    (AddRepositoryOutcome.ClearTextCredential, _) => Results.BadRequest(new
                        { error = $"The repository was not added. {RepositoryUrl.ClearTextCredentialRefusal}" }),
                    (AddRepositoryOutcome.ProjectIsFull, _) => Results.Conflict(new
                    {
                        error =
                            $"Project '{project.Slug}' was created as a single-repository project and already has its repository. "
                            + "That cannot be changed, because its files are named without a repository slug. Create another project for a second repository."
                    }),
                    _ => Results.Conflict(new
                        { error = $"Project '{project.Slug}' already has a repository with slug '{request.Slug}'." })
                });

        project.MapGet("/repositories",
            async (Project project, ControlDatabase control, CancellationToken ct) =>
                (await control.ListRepositoriesAsync(project.Slug, ct))
                .Select(r => new RepositoryResponse(r.Slug, r.Url, r.HasCredential)));

        // The overview page's setting (#216). Nothing is rebuilt: the page reads it on its next load.
        project.MapGet("/excluded-paths", async (Project project, ControlDatabase control, CancellationToken ct) =>
            new ExcludedPathsBody(await control.ExcludedPathsAsync(project.Slug, ct)));

        project.MapPut("/excluded-paths",
            async (Project project, ExcludedPathsBody request, ControlDatabase control, CancellationToken ct) =>
                await control.SetExcludedPathsAsync(project.Slug, request.Patterns, ct) switch
                {
                    ({ } saved, _) => Results.Ok(new ExcludedPathsBody(saved)),
                    (_, var problem) => Results.BadRequest(new { error = problem })
                });
    }
}
