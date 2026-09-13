using System.ComponentModel;
using CodeExplorer;
using ModelContextProtocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ControlDatabase>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<ProjectTools>();

var app = builder.Build();

// Operator endpoints. Authentication is off until the ticket that adds it (ADR-0004: absent
// configuration means an unauthenticated server).
var api = app.MapGroup("/api");
api.MapPost("/projects", async (CreateProjectRequest request, ControlDatabase control, CancellationToken ct) =>
    await control.CreateAsync(request.Slug, request.Name, ct) switch
    {
        CreateProjectOutcome.Created => Results.Created($"/projects/{request.Slug}/mcp",
            new Project(request.Slug, request.Name!.Trim())),
        CreateProjectOutcome.InvalidSlug => Results.BadRequest(new { error = ControlDatabase.SlugRule }),
        CreateProjectOutcome.MissingName => Results.BadRequest(new { error = "Name is required." }),
        _ => Results.Conflict(new { error = $"A project with slug '{request.Slug}' already exists." })
    });

// One MCP endpoint per project (ADR-0002). The filter binds the project from the route before the
// SDK sees the request, so every tool in the session answers for that project and nothing else.
// Once authentication arrives, the unknown-slug 404 moves to OnResourceMetadataRequest (ADR-0004)
// so protected-resource metadata is path-scoped as well; until then this filter stands in.
var projects = app.MapGroup("/projects/{slug}");
projects.AddEndpointFilter(async (context, next) =>
{
    string slug = (string)context.HttpContext.GetRouteValue("slug")!;
    var project = await context.HttpContext.RequestServices.GetRequiredService<ControlDatabase>()
        .FindAsync(slug, context.HttpContext.RequestAborted);
    if (project is null)
        return Results.NotFound(new
            { error = $"No project with slug '{slug}'. Create it with POST /api/projects first." });

    context.HttpContext.Items[ProjectTools.ProjectItemKey] = project;
    return await next(context);
});
projects.MapMcp("/mcp");

app.Run();

internal sealed record CreateProjectRequest(string Slug, string? Name);

/// <summary>Marker so the tests can host the app through <c>WebApplicationFactory</c>.</summary>
public partial class Program;

[McpServerToolType]
internal sealed class ProjectTools(IHttpContextAccessor httpContextAccessor)
{
    public const string ProjectItemKey = "CodeExplorer.Project";

    [McpServerTool(Name = "which_project")]
    [Description(
        "Reports which project this MCP endpoint is bound to. The project comes from the URL you connected to, not from an argument; use it to confirm the server before searching.")]
    public string WhichProject()
    {
        var project = BoundProject();
        return $"This endpoint serves the project '{project.Name}' (slug: {project.Slug}).";
    }

    /// <summary>
    ///     The Streamable HTTP transport runs each handler inside the HTTP request that carried it, so
    ///     the project the route filter resolved is on the current context. If the SDK ever dispatches
    ///     off-request this fails loudly rather than binding to nothing.
    /// </summary>
    private Project BoundProject() =>
        httpContextAccessor.HttpContext?.Items[ProjectItemKey] as Project
        ?? throw new McpException("No project is bound to this request. Connect through /projects/{slug}/mcp.");
}
