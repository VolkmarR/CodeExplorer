using ModelContextProtocol;

namespace CodeExplorer;

/// <summary>
///     The project a request is bound to (ADR-0002). A route group declares with <see cref="BindProject" />
///     that its endpoints are one project's, <see cref="UseBoundProject" /> resolves the
///     <c>{project}</c> route value once per request and stores the project here, and every handler
///     and tool reads it back — a handler by declaring a <see cref="Project" /> parameter, a tool
///     through <see cref="Get" />. "Does this project exist" is therefore asked in one place and
///     answered with one sentence, and a handler is handed a project, never a slug.
///     It is a middleware and not an endpoint filter because minimal APIs bind a handler's parameters
///     before its filters run: a project a filter had resolved would not be there yet when the
///     parameter asked for it. The middleware runs after routing, so the endpoint and its route values
///     are known, and before the endpoint, so the parameter finds what it stored.
///     It is not the only place a slug is checked: <see cref="Authentication" /> refuses a
///     protected-resource document for one that is no project, because that answer has to be given
///     before any token exists and is served inside the authentication handler, where no route value
///     exists yet. The two are the same question asked at two points of the same request, not a
///     duplicate — this one runs after authorization and binds; that one runs instead of it and only
///     says no.
/// </summary>
internal static class BoundProject
{
    public const string ItemKey = "CodeExplorer.Project";

    /// <summary>The route value both groups name the project by; the URLs themselves differ.</summary>
    public const string RouteValue = "project";

    /// <summary>The one sentence for a slug that is no project, wherever it is asked.</summary>
    public static string NotFound(string slug) =>
        $"No project with slug '{slug}'. Create it with POST /api/projects first.";

    /// <summary>
    ///     Marks every endpoint of the group as bound to the <c>{project}</c> in its route. The group's
    ///     template has to carry that parameter; nothing checks it earlier than the first request.
    /// </summary>
    public static RouteGroupBuilder BindProject(this RouteGroupBuilder projects) =>
        projects.WithMetadata(BindsProject.Instance);

    /// <summary>
    ///     The operator API's per-project group: everything under <c>/api/projects/{project}</c>, bound.
    ///     Each module maps its own onto <paramref name="api" />, which keeps a module's endpoints in its
    ///     folder (ADR-0005) while the binding is declared once, here.
    /// </summary>
    public static RouteGroupBuilder MapProject(this RouteGroupBuilder api) =>
        api.MapGroup($"/projects/{{{RouteValue}}}").BindProject();

    /// <summary>
    ///     Resolves the bound project for a marked endpoint, or answers 404 in its place. Placed after
    ///     authentication and authorization, so an unauthenticated caller learns nothing about which
    ///     slugs exist.
    /// </summary>
    public static IApplicationBuilder UseBoundProject(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (http.GetEndpoint()?.Metadata.GetMetadata<BindsProject>() is null)
            {
                await next(http);
                return;
            }

            string slug = (string)http.GetRouteValue(RouteValue)!;
            var project = await http.RequestServices.GetRequiredService<ControlDatabase>()
                .FindAsync(slug, http.RequestAborted);
            if (project is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = NotFound(slug) }, http.RequestAborted);
                return;
            }

            http.Items[ItemKey] = project;
            await next(http);
        });

    /// <summary>
    ///     The Streamable HTTP transport runs each handler inside the HTTP request that carried it, so
    ///     the project the middleware resolved is on the current context. If the SDK ever dispatches
    ///     off-request this fails loudly rather than binding to nothing.
    /// </summary>
    public static Project Get(IHttpContextAccessor httpContextAccessor) =>
        httpContextAccessor.HttpContext?.Items[ItemKey] as Project
        ?? throw new McpException("No project is bound to this request. Connect through /projects/{project}/mcp.");

    /// <summary>What a handler's <see cref="Project" /> parameter reads; null on an endpoint no group bound.</summary>
    public static Project? Bound(HttpContext http) => http.Items[ItemKey] as Project;

    /// <summary>
    ///     The bound project's slug, or null where there is none. The reading <see cref="Get" /> refuses
    ///     to make, for the one caller that must not throw: telemetry. A measurement is not worth an
    ///     exception on a call that would otherwise have answered, and an untagged one is not worth
    ///     recording (CODING_STANDARDS, Telemetry) — so a null here means the recording is skipped
    ///     rather than made without a project.
    /// </summary>
    public static string? SlugOrNull(IServiceProvider? services) =>
        (services?.GetService<IHttpContextAccessor>()?.HttpContext?.Items[ItemKey] as Project)?.Slug;

    /// <summary>Endpoint metadata: this endpoint is one project's. One instance, because it carries nothing.</summary>
    private sealed class BindsProject
    {
        public static readonly BindsProject Instance = new();
    }
}
