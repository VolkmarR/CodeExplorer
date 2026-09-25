using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Primitives;

namespace CodeExplorer.Infrastructure;

/// <summary>
///     Which requests the server answers by where they come from (GHSA-qxhv-3r9w-q8h4). A page that
///     rebinds its own name to 127.0.0.1 is same-origin with the server and told apart only by its
///     Host header, so an unauthenticated server answers loopback names alone. A page on another
///     origin that posts to the server without reading the answer is told apart by its Origin header,
///     which the MCP specification asks a Streamable HTTP server to check for the same reason.
/// </summary>
public static class RequestOrigin
{
    /// <summary>The framework's own setting, read by its host filtering middleware.</summary>
    public const string AllowedHostsSetting = "AllowedHosts";

    /// <summary>
    ///     What a browser and an agent on the same machine call a server on it, and no name a page can
    ///     be served under.
    /// </summary>
    private static readonly string[] _loopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    ///     Restricts host filtering to the loopback names when authentication is off and no
    ///     <c>AllowedHosts</c> is configured, and says whether it did. With a tenant a rebound page is
    ///     one more anonymous caller the fallback policy refuses, so the framework's default stays.
    ///     A <c>Configure</c> and not a <c>PostConfigure</c>: the framework's own post-configuration
    ///     fills <c>AllowedHosts</c> in from configuration only when nothing has set it.
    /// </summary>
    public static bool AddLoopbackHosts(this WebApplicationBuilder builder, AuthenticationSettings authentication)
    {
        bool loopbackOnly = !authentication.Enabled
                            && string.IsNullOrWhiteSpace(builder.Configuration[AllowedHostsSetting]);
        if (loopbackOnly)
            builder.Services.Configure<HostFilteringOptions>(options => options.AllowedHosts = [.. _loopbackHosts]);
        return loopbackOnly;
    }

    /// <summary>
    ///     Said at start because a server behind IIS or a proxy with authentication off answers its
    ///     real host name with a 400 and nothing else, and this line is what names the fix.
    /// </summary>
    public static void ReportLoopbackOnly(ILogger logger) =>
        logger.LogWarning(
            "Only loopback host names ({Hosts}) are answered, because authentication is off and no "
            + "AllowedHosts is configured. Set AllowedHosts to serve other names", string.Join(", ", _loopbackHosts));

    /// <summary>
    ///     Marks a group whose endpoints refuse a foreign origin. Metadata rather than a path match, so
    ///     what is guarded is exactly what routing reaches, in whatever spelling it forgives.
    /// </summary>
    public static TBuilder SameOriginOnly<TBuilder>(this TBuilder endpoints) where TBuilder : IEndpointConventionBuilder =>
        endpoints.WithMetadata(SameOriginMarker.Instance);

    /// <summary>
    ///     Refuses a request to a <see cref="SameOriginOnly{TBuilder}" /> endpoint whose <c>Origin</c> is
    ///     present and is not this server's own. Absent is served: a script, an agent and a same-origin
    ///     GET send none, and a browser sends one on every request that could change something
    ///     cross-origin. The endpoint is known here because the host runs routing before any of this.
    /// </summary>
    public static void UseSameOriginOnly(this WebApplication app) =>
        app.Use(static async (context, next) =>
        {
            var origin = context.Request.Headers.Origin;
            if (origin.Count == 0
                || context.GetEndpoint()?.Metadata.GetMetadata<SameOriginMarker>() is null
                || IsOwn(context.Request, origin))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new { error = $"Refused a request from origin '{origin}': this server answers its own origin only." },
                context.RequestAborted);
        });

    /// <summary>
    ///     One origin, parsed, naming this request's own scheme, host and port. More than one value, or
    ///     <c>null</c> (a sandboxed frame, a file), is not this server. The port is compared resolved,
    ///     so <c>http://localhost</c> is the origin of a request to <c>localhost:80</c>.
    /// </summary>
    private static bool IsOwn(HttpRequest request, StringValues origin)
    {
        if (origin is not [{ } value]
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.AbsolutePath != "/")
            return false;

        int port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return parsed.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
               && parsed.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
               && parsed.Port == port;
    }

    private sealed class SameOriginMarker
    {
        public static readonly SameOriginMarker Instance = new();
    }
}
