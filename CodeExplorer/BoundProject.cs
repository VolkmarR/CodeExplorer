using ModelContextProtocol;

namespace CodeExplorer;

/// <summary>
///     The project a request is bound to (ADR-0002). <see cref="BindProject" /> resolves it from the
///     route and stores it here; every MCP tool class reads it back through <see cref="Get" />, so both
///     halves of the binding rule live in one place.
/// </summary>
internal static class BoundProject
{
    public const string ItemKey = "CodeExplorer.Project";

    /// <summary>
    ///     Resolves the <c>{slug}</c> route value to a project before the MCP SDK sees the request, so
    ///     every tool in the session answers for that project and nothing else. Once authentication
    ///     arrives, the unknown-slug 404 moves to <c>OnResourceMetadataRequest</c> (ADR-0004) so
    ///     protected-resource metadata is path-scoped as well; until then this filter stands in.
    /// </summary>
    public static RouteGroupBuilder BindProject(this RouteGroupBuilder projects)
    {
        projects.AddEndpointFilter(async (context, next) =>
        {
            string slug = (string)context.HttpContext.GetRouteValue("slug")!;
            var project = await context.HttpContext.RequestServices.GetRequiredService<ControlDatabase>()
                .FindAsync(slug, context.HttpContext.RequestAborted);
            if (project is null)
                return Results.NotFound(new
                    { error = $"No project with slug '{slug}'. Create it with POST /api/projects first." });

            context.HttpContext.Items[ItemKey] = project;
            return await next(context);
        });
        return projects;
    }

    /// <summary>
    ///     The Streamable HTTP transport runs each handler inside the HTTP request that carried it, so
    ///     the project the route filter resolved is on the current context. If the SDK ever dispatches
    ///     off-request this fails loudly rather than binding to nothing.
    /// </summary>
    public static Project Get(IHttpContextAccessor httpContextAccessor) =>
        httpContextAccessor.HttpContext?.Items[ItemKey] as Project
        ?? throw new McpException("No project is bound to this request. Connect through /projects/{slug}/mcp.");
}
