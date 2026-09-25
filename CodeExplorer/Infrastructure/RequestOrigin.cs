using Microsoft.AspNetCore.HostFiltering;

namespace CodeExplorer.Infrastructure;

/// <summary>What <see cref="RequestOrigin.AddLoopbackHosts" /> decided at start, for the log line.</summary>
/// <param name="LoopbackOnly">Whether only the loopback host names are answered.</param>
public sealed record HostRestriction(bool LoopbackOnly);

/// <summary>
///     Which requests the server answers by where they come from, rather than by who sent them
///     (GHSA-qxhv-3r9w-q8h4). Two checks, because a browser can reach an unauthenticated server two
///     ways. A page can rebind its own host name to 127.0.0.1 and so become same-origin with it; only
///     the Host header tells that request apart, so with authentication off the server answers
///     loopback names alone. And a page on any origin can post to <c>http://localhost:5000</c>
///     without reading the answer, which still creates, refreshes or deletes; the Origin header tells
///     that one apart, so the API and the MCP endpoints refuse an origin that is not their own. The
///     MCP specification asks a Streamable HTTP server for the second check for the same reason.
///     In Infrastructure/ beside <see cref="Authentication" />, and for its reason: who may call
///     belongs to no concept in CONTEXT.md.
/// </summary>
public static class RequestOrigin
{
    /// <summary>The framework's own setting, read by its host filtering middleware.</summary>
    public const string AllowedHostsSetting = "AllowedHosts";

    /// <summary>
    ///     What a developer's browser and an agent on the same machine call a server on it. Nothing
    ///     else is a loopback name that a rebinding page could not also claim: a page controls the name
    ///     it is served under, and it cannot be served under one of these.
    /// </summary>
    private static readonly string[] _loopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    ///     Restricts host filtering to the loopback names when authentication is off and the operator
    ///     named no <c>AllowedHosts</c> of their own. With a tenant, a rebound page is just another
    ///     anonymous caller the fallback policy already refuses, so the framework's allow-everything
    ///     default stays. Decided once at start, as authentication is: a restart is what changes it.
    ///     A <c>Configure</c> and not a <c>PostConfigure</c>: the framework's own post-configuration
    ///     fills in <c>AllowedHosts</c> from configuration only when nothing has set it, so an absent
    ///     setting finds this list there instead of <c>*</c>.
    /// </summary>
    public static HostRestriction AddLoopbackHosts(this WebApplicationBuilder builder,
        AuthenticationSettings authentication)
    {
        var restriction = new HostRestriction(!authentication.Enabled
                                              && string.IsNullOrWhiteSpace(builder.Configuration[AllowedHostsSetting]));
        if (restriction.LoopbackOnly)
            builder.Services.Configure<HostFilteringOptions>(options => options.AllowedHosts = [.. _loopbackHosts]);
        return restriction;
    }

    /// <summary>
    ///     Said at start because a server behind IIS or a proxy with authentication off answers its
    ///     real host name with a 400 and nothing else, and this line is what names the fix.
    /// </summary>
    public static void Report(this HostRestriction restriction, ILogger logger)
    {
        if (restriction.LoopbackOnly)
            logger.LogWarning(
                "Only loopback host names ({Hosts}) are answered, because authentication is off and no "
                + "{Setting} is configured. Set {Setting} to serve other names",
                string.Join(", ", _loopbackHosts), AllowedHostsSetting, AllowedHostsSetting);
    }

    /// <summary>
    ///     Refuses a request to the API or an MCP endpoint whose <c>Origin</c> is present and is not
    ///     this server's own. Absent is served: a script, an agent and a same-origin GET send none, and
    ///     a browser sends one on every request that could change something cross-origin.
    /// </summary>
    public static void UseSameOriginOnly(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Headers.Origin.Count == 0 || !Guarded(request.Path) || IsOwn(request))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new { error = $"Refused a request from origin '{request.Headers.Origin}': this server answers "
                              + "its own origin only." },
                context.RequestAborted);
        });

    /// <summary>
    ///     <c>/api</c> and <c>/projects/{slug}/mcp</c>. The literals are compared case-insensitively
    ///     because routing is, so a different spelling cannot reach the endpoint around this check.
    /// </summary>
    private static bool Guarded(PathString path)
    {
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWithSegments("/projects", StringComparison.OrdinalIgnoreCase, out var rest)) return false;

        // Anything with an mcp segment under /projects, rather than exactly the endpoint's shape: a
        // doubled or trailing slash that routing forgives must not be a way past.
        foreach (string segment in (rest.Value ?? string.Empty).Split('/'))
            if (segment.Equals("mcp", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    ///     One origin, parsed, naming this request's own scheme, host and port. More than one value, or
    ///     <c>null</c> (a sandboxed frame, a file), is not this server. The port is compared resolved,
    ///     so <c>http://localhost</c> is the origin of a request to <c>localhost:80</c>.
    /// </summary>
    private static bool IsOwn(HttpRequest request)
    {
        if (request.Headers.Origin is not [{ } value]
            || !Uri.TryCreate(value, UriKind.Absolute, out var origin)
            || origin.AbsolutePath != "/")
            return false;

        int port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
               && origin.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
               && origin.Port == port;
    }
}
