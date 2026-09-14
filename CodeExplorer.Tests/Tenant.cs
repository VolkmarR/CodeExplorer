using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CodeExplorer.Tests;

/// <summary>
///     A tenant that does not exist, so that the half of #12 which is this server's own wiring can be
///     tested without one that does. What is faked is exactly two things: where the signing keys and
///     the authorization endpoint come from, which a real deployment fetches over the network, and the
///     signature on a token, which a real tenant applies with a key no test can hold.
///     Everything downstream of those is the real thing — the scheme selector, the fallback policy, the
///     MCP challenge, the cookie and its key ring — which is the whole of what this ticket built. A
///     test that mocked further in would only prove the mock.
/// </summary>
public static class Tenant
{
    public const string ClientId = "11111111-1111-1111-1111-111111111111";
    public const string TenantId = "22222222-2222-2222-2222-222222222222";

    /// <summary>
    ///     A cloud that resolves to nothing, on purpose: a test that reached
    ///     <c>login.microsoftonline.com</c> would pass on a laptop and hang on a build agent.
    /// </summary>
    public const string Instance = "https://login.test.invalid/";

    public const string Scope = "api://codeexplorer/Projects.Read";

    /// <summary>The v2.0 authority the metadata document should name, spelled out rather than composed.</summary>
    public const string Authority = $"{Instance}{TenantId}/v2.0";

    /// <summary>
    ///     Symmetric because a test needs to sign with it, where a tenant signs with a private key and
    ///     publishes the public half. Which algorithm signs a token is not what any of this is about:
    ///     what is under test starts once the handler has accepted one.
    /// </summary>
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("a signing key that is long enough for HS256")) { KeyId = "test" };

    /// <summary>What an appsettings pointing at this tenant would say.</summary>
    public static Dictionary<string, string> Configuration => new()
    {
        [Authentication.InstanceSetting] = Instance,
        [Authentication.TenantIdSetting] = TenantId,
        [Authentication.ClientIdSetting] = ClientId,
        [Authentication.ScopesSetting] = Scope,
        // Never used — nothing here redeems an authorization code — but Microsoft.Identity.Web treats a
        // confidential client with no secret at all as a misconfiguration, and it is right to.
        [$"{Authentication.Section}:ClientSecret"] = "not a real secret"
    };

    /// <summary>
    ///     Replaces both handlers' discovery with a fixed document. It has to replace the configuration
    ///     <em>manager</em> and not just the configuration: the framework's own post-configure has
    ///     already turned <c>Options.Configuration</c> into a manager by the time a test's post-configure
    ///     runs, so assigning the configuration at this point is read by nothing.
    /// </summary>
    public static void StubDiscovery(IServiceCollection services)
    {
        var discovered = new OpenIdConnectConfiguration
        {
            Issuer = Authority,
            AuthorizationEndpoint = $"{Instance}{TenantId}/oauth2/v2.0/authorize",
            TokenEndpoint = $"{Instance}{TenantId}/oauth2/v2.0/token",
            EndSessionEndpoint = $"{Instance}{TenantId}/oauth2/v2.0/logout"
        };
        discovered.SigningKeys.Add(SigningKey);

        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.ConfigurationManager = Fixed(discovered);
            // Replaced wholesale rather than adjusted: Microsoft.Identity.Web installs an issuer
            // validator that asks the real cloud which tenants are legitimate, and it is the one part
            // of the chain that cannot be pointed at a tenant that does not exist.
            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = SigningKey,
                NameClaimType = "name",
                ValidAudiences = [ClientId, $"api://{ClientId}"],
                ValidIssuer = Authority
            };
        });

        services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
            options => options.ConfigurationManager = Fixed(discovered));
    }

    private static StaticConfigurationManager<OpenIdConnectConfiguration> Fixed(OpenIdConnectConfiguration
        configuration) => new(configuration);

    /// <summary>
    ///     A token this tenant would have issued. <c>scp</c> is not decoration: Microsoft.Identity.Web
    ///     rejects a bearer token carrying neither a scope nor a role, on the grounds that it was minted
    ///     for something other than this API.
    /// </summary>
    public static string Token(string name = "An Operator") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = ClientId,
            Claims = new Dictionary<string, object>
            {
                ["name"] = name,
                ["oid"] = "33333333-3333-3333-3333-333333333333",
                ["scp"] = "Projects.Read",
                ["sub"] = "33333333-3333-3333-3333-333333333333",
                ["tid"] = TenantId
            },
            Expires = DateTime.UtcNow.AddMinutes(10),
            Issuer = Authority,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });

    /// <summary>
    ///     A sign-in cookie for a host, forged through the host's own ticket format. That is what makes
    ///     it worth doing: the format is the Data Protection key ring under the purposes the cookie
    ///     handler uses, so a cookie made here is a cookie the handler made, and one that still reads
    ///     after a restart is the key ring surviving one.
    /// </summary>
    public static string Cookie(IServiceProvider services, string name = "An Operator")
    {
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role);
        identity.AddClaim(new Claim("name", name));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "33333333-3333-3333-3333-333333333333"));

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity),
            CookieAuthenticationDefaults.AuthenticationScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }
}
