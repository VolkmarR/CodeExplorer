using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace CodeExplorer;

/// <summary>
///     What the server knows about the Entra application it belongs to, which is one question — is
///     there one — and two answers that only matter once there is. Read once at start rather than at
///     each use, because a half-configured tenant has to stop the server rather than the first
///     request that happens to need it.
/// </summary>
/// <param name="ClientId">
///     The application registration, and the setting that decides everything here: with no client id
///     there is no application to check a token against, so absent means authentication is off
///     (ADR-0004) rather than misconfigured.
/// </param>
/// <param name="Authority">
///     The v2.0 authority tokens are issued by, composed from the instance and the tenant. It is what
///     a protected-resource document names as its authorization server, which is the only thing that
///     lets an MCP client find the tenant at all.
/// </param>
/// <param name="Scopes">
///     What the protected-resource document advertises as usable against this API, and nothing more:
///     no scope is enforced, because every project shares one audience and the requirement is
///     protection from unauthenticated callers and nothing finer.
/// </param>
public sealed record AuthenticationSettings(string? ClientId, Uri? Authority, IReadOnlyList<string> Scopes)
{
    /// <summary>
    ///     Whether there is a tenant to check anything against. <see cref="Authority" /> is non-null
    ///     exactly when this is true, which the annotation says so that the composition root and the
    ///     metadata document can read it without a null-forgiving operator apiece.
    /// </summary>
    [MemberNotNullWhen(true, nameof(ClientId), nameof(Authority))]
    public bool Enabled => ClientId is not null;
}

/// <summary>
///     Entra sign-in for two kinds of caller over one library (ADR-0004). An MCP client or an API
///     script presents a bearer token; the web UI holds a server-issued cookie and never sees a
///     token at all. Both end at the same fallback policy, so an endpoint is protected by existing
///     rather than by remembering to say so.
///     The 401 a project endpoint returns is the interesting one: an MCP client has no way to learn
///     which tenant to ask until the challenge names a protected-resource document, and that document
///     has to be per project because a project's endpoint is its own resource (ADR-0002). The SDK's
///     own authentication scheme does both halves — see <see cref="Describe" /> for what it does and
///     what this code still has to supply.
///     At the root rather than in a module folder for the reason <see cref="KeyRing" /> is: ADR-0005's
///     folders are the concepts in CONTEXT.md, and who is calling belongs to no one of them.
/// </summary>
public static class Authentication
{
    /// <summary>
    ///     The configuration section, spelled the way Microsoft.Identity.Web expects: it binds
    ///     <c>Instance</c>, <c>TenantId</c>, <c>ClientId</c> and <c>ClientSecret</c> out of this
    ///     section itself, so the settings read below are read from the same place rather than
    ///     duplicated under a name of this project's choosing.
    /// </summary>
    public const string Section = "AzureAd";

    public const string ClientIdSetting = $"{Section}:ClientId";
    public const string TenantIdSetting = $"{Section}:TenantId";
    public const string InstanceSetting = $"{Section}:Instance";

    /// <summary>Space-separated, as a scope list is everywhere else in OAuth.</summary>
    public const string ScopesSetting = $"{Section}:Scopes";

    /// <summary>The public cloud, which is where a tenant is unless someone says otherwise.</summary>
    private const string DefaultInstance = "https://login.microsoftonline.com/";

    /// <summary>
    ///     The scheme every request is authenticated under: not a handler of its own but a selector
    ///     over the two that are. A request carrying a bearer token is a client or a script and is
    ///     checked as a token; anything else is the browser and is checked as a cookie. One default
    ///     scheme rather than a policy per endpoint group is what lets the API accept either without
    ///     each endpoint naming both.
    /// </summary>
    public const string SelectorScheme = "CodeExplorer";

    /// <summary>
    ///     The MCP endpoints' policy. It cannot be the fallback policy, because what makes it
    ///     different is the challenge and not the check: the same token is accepted either way, but
    ///     only this scheme answers the 401 with the resource metadata an MCP client needs.
    /// </summary>
    public const string McpPolicy = "mcp";

    public const string SignInPath = "/api/auth/signin";
    public const string SignOutPath = "/api/auth/signout";

    /// <summary>
    ///     Named rather than left at the framework's <c>ReturnUrl</c> so that the cookie handler's
    ///     redirect and the sign-in endpoint's own parameter are one spelling. A mismatch here is
    ///     invisible: the sign-in still works and simply lands on the home page every time.
    /// </summary>
    private const string ReturnUrlParameter = "returnUrl";

    /// <summary>Where RFC 9728 documents live, and the prefix the SDK's handler answers under.</summary>
    private const string MetadataPrefix = "/.well-known/oauth-protected-resource";

    /// <summary>
    ///     Reads the tenant, or reports that there is none. A client id with no tenant is the one
    ///     shape that is neither on nor off, and it stops the server here rather than failing on the
    ///     first token: Microsoft.Identity.Web would otherwise come up and reject everything with a
    ///     message about metadata retrieval.
    /// </summary>
    public static AuthenticationSettings Read(IConfiguration configuration)
    {
        string? clientId = Trimmed(configuration[ClientIdSetting]);
        if (clientId is null) return new AuthenticationSettings(null, null, []);

        string tenant = Trimmed(configuration[TenantIdSetting])
                        ?? throw new InvalidOperationException(
                            $"{ClientIdSetting} is set but {TenantIdSetting} is not, so there is no tenant to check "
                            + "a token against. Give it the directory id, or remove both to run unauthenticated.");

        var instance = Setting.Url(configuration, InstanceSetting,
                           $"Give it a cloud such as {DefaultInstance}, or remove it to use that one.")
                       ?? new Uri(DefaultInstance);

        // Composed rather than concatenated, and the trailing slash forced first: `new Uri(base,
        // relative)` replaces the last segment of a base that has none, so an instance written
        // without one would silently yield an authority missing the cloud's path.
        string root = instance.OriginalString.EndsWith('/') ? instance.OriginalString : instance.OriginalString + "/";
        var authority = new Uri(new Uri(root), $"{tenant}/v2.0");

        string[] scopes = (Trimmed(configuration[ScopesSetting]) ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new AuthenticationSettings(clientId, authority, scopes);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    ///     Registers the two flows and the fallback policy, or nothing at all. Nothing at all is a
    ///     whole shape and not a degraded one (ADR-0004): a plain <c>dotnet run</c> with an empty
    ///     appsettings has no authentication middleware in its pipeline, so local work stays one
    ///     command and the unauthenticated path is the one the tests already cover.
    /// </summary>
    public static AuthenticationSettings AddAuthentication(this WebApplicationBuilder builder)
    {
        var settings = Read(builder.Configuration);
        if (!settings.Enabled) return settings;

        var authentication = builder.Services.AddAuthentication(SelectorScheme);
        authentication.AddPolicyScheme(SelectorScheme, "Bearer token or sign-in cookie",
            options => options.ForwardDefaultSelector = Choose);
        authentication.AddMicrosoftIdentityWebApi(builder.Configuration, Section);
        authentication.AddMicrosoftIdentityWebApp(builder.Configuration, Section);
        authentication.AddMcp(options => Describe(options, settings.Authority, settings.Scopes));

        // After Microsoft.Identity.Web rather than through its own hook, because it sets a LoginPath
        // of its own: /MicrosoftIdentity/Account/SignIn, a route that ships in Microsoft.Identity.Web.UI,
        // which ADR-0004 does not take. Left alone, every unauthenticated browser would reach a 404.
        builder.Services.PostConfigure<CookieAuthenticationOptions>(
            CookieAuthenticationDefaults.AuthenticationScheme, RouteCookieChallenges);

        builder.Services.AddAuthorizationBuilder()
            // Nothing is reachable anonymously unless it says so. The exceptions are few and all of
            // them are about getting signed in: the sign-in endpoints below, and the metadata document,
            // which the SDK's handler serves from the authentication middleware and which therefore
            // never reaches an endpoint or this policy at all.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(McpPolicy, policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());

        return settings;
    }

    /// <summary>
    ///     Which of the two handlers a request belongs to. The header and not the path decides it, so
    ///     that one API serves both a script holding a token and the browser holding a cookie, and so
    ///     that a request with neither is treated as the browser — which is the one that can be sent
    ///     somewhere to fix it.
    /// </summary>
    private static string Choose(HttpContext context) =>
        context.Request.Headers.Authorization.Any(value =>
            value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            ? JwtBearerDefaults.AuthenticationScheme
            : CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    ///     The protected-resource document, and the one thing the SDK cannot know. Left with no
    ///     <c>ResourceMetadataUri</c>, its handler does the path-scoping ADR-0002 needs on its own: it
    ///     answers any path under the well-known prefix, reads the resource URL off the remainder, and
    ///     names that same URL in the <c>WWW-Authenticate</c> header of every challenge. What it will
    ///     not do is tell a real project from a typo — it would serve a document for any path at all —
    ///     so the event below is where an unknown slug becomes a 404.
    /// </summary>
    private static void Describe(McpAuthenticationOptions options, Uri authority, IReadOnlyList<string> scopes)
    {
        // Resource is deliberately unset: the handler fills it in per request from the path it was
        // asked for, and a value here would pin every project's document to one project's URL.
        options.ResourceMetadata = new ProtectedResourceMetadata
        {
            AuthorizationServers = [authority.ToString()],
            BearerMethodsSupported = ["header"],
            ScopesSupported = [.. scopes]
        };
        options.Events.OnResourceMetadataRequest = NameTheProjectOrRefuse;
    }

    /// <summary>
    ///     Turns an unknown slug into a 404 and names the project in the document it does serve. The
    ///     404 matters more than it looks: a document served for every path would tell a client its
    ///     typo is a resource, and the sign-in it then completes would buy it a token for an endpoint
    ///     that does not exist.
    /// </summary>
    private static async Task NameTheProjectOrRefuse(ResourceMetadataRequestContext context)
    {
        var http = context.HttpContext;
        string? slug = ProjectSlug(http.Request.Path);
        var project = slug is null
            ? null
            : await http.RequestServices.GetRequiredService<ControlDatabase>().FindAsync(slug, http.RequestAborted);

        if (project is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(
                new { error = $"No protected resource at '{http.Request.Path}'. A project's is at "
                              + $"{MetadataPrefix}/projects/{{slug}}/mcp." },
                http.RequestAborted);
            context.HandleResponse();
            return;
        }

        // The handler has already cloned the options' document and filled in the resource URL; the
        // display name is the only part of it that is per project.
        if (context.ResourceMetadata is { } metadata) metadata.ResourceName = project.Name;
    }

    /// <summary>
    ///     The project a metadata request is about, or null when the path is not one project's. Two
    ///     things make this stricter and looser than it looks, and both are about the URL the SDK
    ///     derives from the same remainder and puts in the document as <c>resource</c>.
    ///     Stricter: the remainder is split without dropping empty segments, so
    ///     <c>//projects//x//mcp</c> is refused rather than answered with a <c>resource</c> that routes
    ///     to nothing — which is exactly the "a typo is told it is a resource" failure this exists to
    ///     prevent. And it has to be a whole endpoint path rather than merely start with one, so
    ///     <c>/projects/x/mcp/anything</c> is the 404 it deserves.
    ///     Looser: the two literals are compared case-insensitively, because routing is. A client
    ///     asking for <c>/projects/x/MCP</c> reaches the real endpoint and is challenged with that
    ///     spelling, so refusing the document for it would dead-end discovery with no way to learn the
    ///     authority at all. The slug stays ordinal: a project slug is lowercase by its own rule.
    /// </summary>
    private static string? ProjectSlug(PathString path)
    {
        if (!path.StartsWithSegments(MetadataPrefix, out var resource)) return null;

        // Leading empty entry included: a remainder always begins with the separator, so the expected
        // shape is four segments and an extra empty one is the doubled slash being refused.
        string[] segments = resource.Value?.Split('/') ?? [];
        return segments is ["", var projects, var slug, var mcp]
               && projects.Equals("projects", StringComparison.OrdinalIgnoreCase)
               && mcp.Equals("mcp", StringComparison.OrdinalIgnoreCase)
            ? slug
            : null;
    }

    /// <summary>
    ///     Where an unauthenticated browser is sent, and where an unauthenticated API call is not. A
    ///     302 to the tenant is the right answer to a navigation and the wrong one to a fetch: the
    ///     browser would follow it cross-origin, fail CORS, and the UI would report a network error
    ///     where the truth is that the cookie expired. Under <c>/api</c> the answer is the status code
    ///     and the prose the rest of the API answers with, which the client turns into a sign-in.
    /// </summary>
    private static void RouteCookieChallenges(CookieAuthenticationOptions options)
    {
        options.LoginPath = SignInPath;
        options.LogoutPath = SignOutPath;
        options.ReturnUrlParameter = ReturnUrlParameter;
        options.Events.OnRedirectToLogin = StatusCodeUnderApi(StatusCodes.Status401Unauthorized,
            $"Not signed in. Sign in at {SignInPath}.");
        options.Events.OnRedirectToAccessDenied = StatusCodeUnderApi(StatusCodes.Status403Forbidden,
            "Signed in, but not permitted here.");
    }

    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> StatusCodeUnderApi(
        int status, string error) =>
        async context =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Redirect(context.RedirectUri);
                return;
            }

            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error }, context.HttpContext.RequestAborted);
        };

    /// <summary>
    ///     Sign-in, sign-out, and who is signed in. Three endpoints and no Razor: ADR-0004 leaves
    ///     Microsoft.Identity.Web.UI out because its controllers assume a server-rendered app, and
    ///     what an SPA needs of it is these.
    /// </summary>
    public static void MapAuthentication(this RouteGroupBuilder api, AuthenticationSettings settings)
    {
        // Anonymous as a group: every one of these is something an unauthenticated caller has to be
        // able to reach, or there is no way to stop being one.
        var auth = api.MapGroup("/auth").AllowAnonymous();

        // Mapped whether or not there is a tenant, because the UI asks this before it renders anything
        // about signing in, and a 404 here would be indistinguishable from an old server.
        auth.MapGet("/me", (ClaimsPrincipal user) => Who(settings, user));

        if (!settings.Enabled) return;

        auth.MapGet("/signin", (string? returnUrl) =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = Local(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]));

        // POST, where the sign-in beside it is a GET, because signing out changes state and signing in
        // does not. As a GET it is reachable by anything that can put a URL on a page: an <img> on a
        // hostile site, a browser prefetching the header link, a scanner following it. The cookie is
        // SameSite=Lax, which carries it on a top-level navigation and withholds it from a cross-site
        // POST — so this spelling is what makes a forged sign-out do nothing rather than sign the
        // operator out. The UI therefore submits a form rather than following a link.
        //
        // Both schemes: the cookie so this server forgets the operator, and OpenID Connect so the
        // tenant does too. Dropping only the cookie would let the next sign-in complete silently,
        // which reads as a sign-out that did not work.
        auth.MapPost("/signout", () =>
            Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
    }

    private static AuthenticationStatus Who(AuthenticationSettings settings, ClaimsPrincipal user) =>
        new(settings.Enabled, user.Identity?.IsAuthenticated ?? false,
            // The tenant's display name where there is one, and the sign-in name where there is not:
            // "name" is what an Entra id token carries, and Identity.Name reads a claim that a token
            // configured differently may not have.
            user.FindFirstValue("name") ?? user.Identity?.Name);

    /// <summary>
    ///     A return URL that cannot leave this server. Anything else — an absolute URL, a
    ///     protocol-relative one, the backslash spelling some browsers normalise into one — becomes
    ///     the home page, because a sign-in endpoint that redirects anywhere is a phishing hop that
    ///     arrives with the tenant's own domain in the address bar.
    /// </summary>
    private static string Local(string? returnUrl) =>
        returnUrl is not null
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    /// <summary>
    ///     Applies the MCP policy where there is one. Where there is not, the endpoints stay as they
    ///     were: an endpoint naming a policy that was never registered throws on the first request,
    ///     which would make an unconfigured server worse than unauthenticated.
    /// </summary>
    public static IEndpointConventionBuilder ProtectMcp(this IEndpointConventionBuilder endpoints,
        AuthenticationSettings settings) =>
        settings.Enabled ? endpoints.RequireAuthorization(McpPolicy) : endpoints;

    /// <summary>
    ///     Which shape authentication came up in. Off is warned about rather than noted: it is the
    ///     right answer on a developer machine and the worst possible one in a container, and nothing
    ///     else in the log distinguishes the two.
    /// </summary>
    public static void Report(this AuthenticationSettings settings, ILogger logger)
    {
        if (!settings.Enabled)
        {
            logger.LogWarning(
                "Authentication is off because no {Setting} is configured: every endpoint, including "
                + "every project's MCP endpoint, answers anonymously", ClientIdSetting);
            return;
        }

        // Read into a local first: a Uri argument is an evaluation the analyzer asks to be held back
        // until something is listening (CA1873).
        string authority = settings.Authority.OriginalString;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Authentication is on: bearer tokens and sign-in are checked against {Authority}",
                authority);
    }
}

/// <summary>
///     What the web UI asks before it renders a sign-in control at all.
///     <paramref name="Enabled" /> and <paramref name="SignedIn" /> are two questions and not one: a
///     development server is neither, and a UI that read only the second would offer a sign-in that
///     goes nowhere.
/// </summary>
internal sealed record AuthenticationStatus(bool Enabled, bool SignedIn, string? Name);
