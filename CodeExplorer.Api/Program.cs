using System.ComponentModel;
using CodeExplorer.Api;
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
    request switch
    {
        _ when !ControlDatabase.IsValidSlug(request.Slug) =>
            Results.BadRequest(new { error = "Slug must be 1-64 lowercase letters, digits or hyphens, starting and ending with a letter or digit." }),
        _ when string.IsNullOrWhiteSpace(request.Name) =>
            Results.BadRequest(new { error = "Name is required." }),
        _ when await control.TryCreateAsync(new Project(request.Slug, request.Name.Trim()), ct) =>
            Results.Created($"/projects/{request.Slug}/mcp", new Project(request.Slug, request.Name.Trim())),
        _ => Results.Conflict(new { error = $"A project with slug '{request.Slug}' already exists." }),
    });

// One MCP endpoint per project (ADR-0002). The filter binds the project from the route before the
// SDK sees the request, so every tool in the session answers for that project and nothing else.
var projects = app.MapGroup("/projects/{slug}");
projects.AddEndpointFilter(async (context, next) =>
{
    var slug = (string)context.HttpContext.GetRouteValue("slug")!;
    var project = await context.HttpContext.RequestServices.GetRequiredService<ControlDatabase>()
        .FindAsync(slug, context.HttpContext.RequestAborted);
    if (project is null)
    {
        return Results.NotFound(new { error = $"No project with slug '{slug}'. Create it with POST /api/projects first." });
    }

    context.HttpContext.Items[ProjectTools.ProjectItemKey] = project;
    return await next(context);
});
projects.MapMcp("/mcp");

app.Run();

internal sealed record CreateProjectRequest(string Slug, string Name);

/// <summary>Marker so the tests can host the app through <c>WebApplicationFactory</c>.</summary>
public partial class Program;

[McpServerToolType]
internal sealed class ProjectTools(IHttpContextAccessor httpContextAccessor)
{
    public const string ProjectItemKey = "CodeExplorer.Project";

    [McpServerTool(Name = "which_project")]
    [Description("Reports which project this MCP endpoint is bound to. The project comes from the URL you connected to, not from an argument; use it to confirm the server before searching.")]
    public string WhichProject()
    {
        // The Streamable HTTP transport runs each handler inside the HTTP request that carried it,
        // so the project the route filter resolved is still on the current context.
        var project = (Project)httpContextAccessor.HttpContext!.Items[ProjectItemKey]!;
        return $"This endpoint serves the project '{project.Name}' (slug: {project.Slug}).";
    }
}
