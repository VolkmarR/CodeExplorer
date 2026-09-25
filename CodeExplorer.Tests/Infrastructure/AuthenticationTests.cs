using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Entra sign-in (#12): who gets in, what a caller who does not is told, and the one document that
///     tells an MCP client where to sign in at all.
///     The tenant is <see cref="Tenant" />'s, which does not exist; what that fakes and why it is
///     still worth asserting on is written there. The other half of this ticket is the half every
///     other test class covers: an unconfigured server behaves exactly as it did, and the whole suite
///     running unauthenticated is the proof.
/// </summary>
public sealed class AuthenticationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Files = "src/a.cs";

    private static Dictionary<string, Dictionary<string, string>> Repository =>
        new() { ["one"] = new Dictionary<string, string> { [Files] = "class Widget;\n" } };

    [Fact]
    public void Absent_configuration_is_an_answer_and_a_half_configured_tenant_is_not()
    {
        var off = Authentication.Read(Settings.Of([]));
        Assert.False(off.Enabled);
        Assert.Null(off.Authority);

        var on = Authentication.Read(Settings.Of(Tenant.Configuration.ToDictionary(e => e.Key, e => (string?)e.Value)));
        Assert.True(on.Enabled);
        Assert.Equal(Tenant.Authority, on.Authority.ToString());
        Assert.Equal([Tenant.Scope], on.Scopes);

        // The one shape that is neither on nor off. It stops the server rather than letting every
        // request fail later with a message about metadata retrieval from a tenant nobody named.
        var half = Assert.Throws<InvalidOperationException>(() =>
            Authentication.Read(Settings.Of(new Dictionary<string, string?>
                { [Authentication.ClientIdSetting] = Tenant.ClientId })));
        Assert.Contains(Authentication.TenantIdSetting, half.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_instance_without_a_trailing_slash_still_names_its_cloud()
    {
        // `new Uri(base, relative)` replaces the last segment of a base that ends without one, so an
        // instance written the natural way would otherwise lose the path of a sovereign cloud.
        var settings = Authentication.Read(Settings.Of(new Dictionary<string, string?>
        {
            [Authentication.ClientIdSetting] = Tenant.ClientId,
            [Authentication.InstanceSetting] = "https://login.test.invalid/some/cloud",
            [Authentication.TenantIdSetting] = Tenant.TenantId
        }));

        Assert.True(settings.Enabled);
        Assert.Equal($"https://login.test.invalid/some/cloud/{Tenant.TenantId}/v2.0", settings.Authority.ToString());
    }

    [Fact]
    public void An_unauthenticated_server_says_so_and_a_configured_one_names_its_authority()
    {
        var off = new LogProbe();
        Authentication.Read(Settings.Of([])).Report(off.CreateLogger(nameof(Authentication)));
        Assert.Contains(Authentication.ClientIdSetting,
            off.Only(LogLevel.Warning, "Authentication is off").Message, StringComparison.Ordinal);

        var on = new LogProbe();
        Authentication.Read(Settings.Of(Tenant.Configuration.ToDictionary(e => e.Key, e => (string?)e.Value)))
            .Report(on.CreateLogger(nameof(Authentication)));
        Assert.Contains(Tenant.Authority, on.Only(LogLevel.Information, "Authentication is on").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_tenant_every_endpoint_still_answers_anonymously()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("open", Repository);

        using var http = host.CreateClient();
        using var api = await http.GetAsync("/api/projects", Ct);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        // And the MCP endpoint, which is the one that matters: a container is what authentication is
        // for, and a plain `dotnet run` is deliberately not one (ADR-0004).
        await using var client = await host.ConnectAsync("open");
        Assert.NotEmpty(await client.ListToolsAsync(cancellationToken: Ct));

        var status = await http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task An_unauthenticated_project_endpoint_names_its_own_resource_metadata()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        await host.IndexedProjectAsync("closed", Repository);

        using var http = host.CreateClient();
        using var response = await http.PostAsync("/projects/closed/mcp", EmptyJson(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Path-scoped, which is the whole point: one project's document names one project's endpoint,
        // and a client that followed a shared one would ask for a token against the wrong resource.
        string challenge = Assert.Single(response.Headers.WwwAuthenticate).ToString();
        Assert.Contains("/.well-known/oauth-protected-resource/projects/closed/mcp", challenge,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_protected_resource_document_is_per_project_and_needs_no_token()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        await host.IndexedProjectAsync("closed", Repository);

        using var http = host.CreateClient();
        var document =
            await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/projects/closed/mcp", Ct);

        Assert.EndsWith("/projects/closed/mcp", document.GetProperty("resource").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(Tenant.Authority,
            Assert.Single(document.GetProperty("authorization_servers").EnumerateArray()).GetString());
        Assert.Equal(Tenant.Scope, Assert.Single(document.GetProperty("scopes_supported").EnumerateArray()).GetString());
        Assert.Equal("closed", document.GetProperty("resource_name").GetString());
    }

    [Fact]
    public async Task A_slug_that_is_no_project_has_no_protected_resource_document()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        await host.IndexedProjectAsync("closed", Repository);

        using var http = host.CreateClient();

        // A document served for any path at all would tell a client its typo is a resource, and the
        // sign-in it then completed would buy a token for an endpoint that does not exist.
        using var unknown =
            await http.GetAsync("/.well-known/oauth-protected-resource/projects/typo/mcp", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // And the path has to be a whole MCP endpoint rather than merely start with one.
        using var deeper =
            await http.GetAsync("/.well-known/oauth-protected-resource/projects/closed/mcp/tools", Ct);
        Assert.Equal(HttpStatusCode.NotFound, deeper.StatusCode);

        // Doubled separators are a typo and not a spelling of the same path. The SDK derives the
        // document's `resource` from this remainder verbatim, so answering here would hand back a URL
        // that routes to nothing — the same failure as the unknown slug, wearing a valid slug.
        using var doubled =
            await http.GetAsync("/.well-known/oauth-protected-resource//projects//closed//mcp", Ct);
        Assert.Equal(HttpStatusCode.NotFound, doubled.StatusCode);
    }

    [Fact]
    public async Task A_project_path_spelled_in_another_case_still_has_its_document()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        await host.IndexedProjectAsync("closed", Repository);

        using var http = host.CreateClient();

        // Routing matches literals case-insensitively, so /projects/closed/MCP reaches the real
        // endpoint and is challenged with that spelling. Refusing the document for it would dead-end
        // discovery: the client would be told where to look and find a 404 when it looked.
        using var challenged = await http.PostAsync("/projects/closed/MCP", EmptyJson(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        Assert.Contains("/projects/closed/MCP", Assert.Single(challenged.Headers.WwwAuthenticate).ToString(),
            StringComparison.Ordinal);

        var document =
            await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/projects/closed/MCP", Ct);
        Assert.Equal("closed", document.GetProperty("resource_name").GetString());
    }

    [Fact]
    public async Task An_unauthenticated_api_call_is_refused_in_prose_rather_than_redirected()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);

        using var http = host.CreateClient();
        using var response = await http.GetAsync("/api/projects", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        // The shape every other failure in this API has, so the web client's one error path covers it:
        // a 302 to the tenant would fail CORS and reach the UI as a network error instead.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains(Authentication.SignInPath, body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browser_asking_for_the_web_ui_is_sent_to_sign_in()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);

        using var http = host.CreateClient();
        using var response = await http.GetAsync("/projects/closed", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        string location = response.Headers.Location!.ToString();
        // Absolute, because that is what the cookie handler builds; what matters is the path it names.
        Assert.StartsWith($"http://localhost{Authentication.SignInPath}", location, StringComparison.Ordinal);
        // Carried through, or every sign-in would land on the home page and the link someone followed
        // would be lost in it.
        Assert.Contains("returnUrl=%2Fprojects%2Fclosed", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_in_goes_to_the_tenant_and_comes_back_to_a_local_page_only()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        using var http = host.CreateClient();

        using var challenge = await http.GetAsync($"{Authentication.SignInPath}?returnUrl=%2Fprojects", Ct);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        string authorize = challenge.Headers.Location!.ToString();
        Assert.StartsWith($"{Tenant.Instance}{Tenant.TenantId}/oauth2/v2.0/authorize", authorize,
            StringComparison.Ordinal);
        Assert.Contains(Tenant.ClientId, authorize, StringComparison.Ordinal);

        // An absolute return URL is dropped rather than obeyed: a sign-in endpoint that redirects
        // anywhere is a phishing hop that arrives carrying this server's own domain.
        using var offsite =
            await http.GetAsync($"{Authentication.SignInPath}?returnUrl=https%3A%2F%2Felsewhere.invalid", Ct);
        Assert.DoesNotContain("elsewhere.invalid", offsite.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/\t/evil.invalid")]
    [InlineData("/\n/evil.invalid")]
    [InlineData("/\r/evil.invalid")]
    [InlineData("/\t\\evil.invalid")]
    [InlineData("/projects\n/x")]
    [InlineData("/projects\u007F")]
    [InlineData("//evil.invalid")]
    [InlineData("/\\evil.invalid")]
    [InlineData("https://evil.invalid")]
    [InlineData("\\\\evil.invalid")]
    [InlineData("")]
    [InlineData(null)]
    public void A_return_url_that_a_browser_could_follow_off_this_server_becomes_the_home_page(string? returnUrl) =>
        // The control characters are the ones that matter: a browser's URL parser strips tab and
        // newline, so "/<TAB>/evil" is followed as "//evil", a protocol-relative URL to another host.
        Assert.Equal("/", Authentication.LocalReturnUrl(returnUrl));

    [Theory]
    [InlineData("/")]
    [InlineData("/projects/acme?x=1")]
    [InlineData("/projects/acme#tree")]
    public void A_local_return_url_is_kept_as_it_was(string returnUrl) =>
        Assert.Equal(returnUrl, Authentication.LocalReturnUrl(returnUrl));

    [Theory]
    [InlineData("/%09/evil.invalid", "/")]
    [InlineData("/%0A/evil.invalid", "/")]
    [InlineData("%2Fprojects%2Facme%3Fx%3D1", "/projects/acme?x=1")]
    public async Task The_page_a_sign_in_comes_back_to_is_the_checked_one(string query, string expected)
    {
        // Through the endpoint rather than the method alone, because "%09" is what the phishing link
        // carries and the query binder decodes it to a tab before the check sees it. The page to come
        // back to travels to the tenant inside the protected state, so it is read back from there.
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        using var http = host.CreateClient();

        using var challenge = await http.GetAsync($"{Authentication.SignInPath}?returnUrl={query}", Ct);

        var authorize = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        var options = host.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        var properties = options.StateDataFormat.Unprotect(authorize["state"]);
        Assert.Equal(expected, properties!.RedirectUri);
    }

    [Fact]
    public async Task Signing_out_is_a_post_so_that_nothing_else_can_do_it()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        // Signed in, because that is the case the method matters for: the request a hostile page can
        // forge is one the operator's own browser makes, carrying the operator's own cookie.
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("Cookie", Tenant.Cookie(host.Services));

        // As a GET it would be reachable by anything that can put a URL on a page — an <img> on a
        // hostile site, a browser prefetching the header link, a scanner following it — and any of
        // those would sign the operator out without being asked to. Asserted as the absence of the
        // sign-out rather than as a status code: the path has no GET of its own, so what answers it is
        // whatever else matches, and which of those it is is not the point.
        using var linked = await http.GetAsync(Authentication.SignOutPath, Ct);
        Assert.DoesNotContain("logout", linked.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        var still = await http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.True(still.GetProperty("signedIn").GetBoolean());

        using var submitted = await http.PostAsync(Authentication.SignOutPath, null, Ct);
        Assert.Equal(HttpStatusCode.Found, submitted.StatusCode);
        // At the tenant, not merely here: forgetting only the cookie would let the next sign-in
        // complete silently, which reads as a sign-out that did not work.
        Assert.StartsWith($"{Tenant.Instance}{Tenant.TenantId}/oauth2/v2.0/logout",
            submitted.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_valid_bearer_token_reaches_the_api_and_the_mcp_tools()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        await host.IndexedProjectAsync("closed", Repository);
        string token = Tenant.Token();

        using var http = host.CreateClient(token);
        using var api = await http.GetAsync("/api/projects", Ct);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        await using var client = await host.ConnectAsync("closed", token);
        string answer = await TestHost.CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*.cs" });
        Assert.Contains(Files, answer, StringComparison.Ordinal);

        // The same token identifies the caller to the web UI's own question, so a script and a browser
        // are answered by one endpoint rather than two.
        var who = await http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.True(who.GetProperty("enabled").GetBoolean());
        Assert.True(who.GetProperty("signedIn").GetBoolean());
        Assert.Equal("An Operator", who.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_token_this_tenant_did_not_sign_is_refused()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);

        using var http = host.CreateClient("not.a.token");
        using var response = await http.GetAsync("/api/projects", Ct);

        // A bearer header is answered as a bearer header even when it is nonsense: the scheme selector
        // reads the header and not the path, so a bad token must not fall through to the cookie
        // handler and come back as a redirect to sign in.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_signed_in_operator_is_still_signed_in_after_a_restart()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        await host.IndexedProjectAsync("closed", Repository);
        string cookie = Tenant.Cookie(host.Services);

        // Everything in memory is gone, which is what a scale to zero leaves. The cookie is ciphertext
        // under the Data Protection key ring (#13), so it reads afterwards only because the key ring
        // outlived the process — and it is honest about which key ring that is: the local one under
        // the user profile, as KeyRingTests says, with the container's wipe being the manual check.
        host.Restart();

        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("Cookie", cookie);

        using var api = await http.GetAsync("/api/projects", Ct);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        var who = await http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.True(who.GetProperty("signedIn").GetBoolean());
        Assert.Equal("An Operator", who.GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_browser_never_holds_a_token()
    {
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);
        string cookie = Tenant.Cookie(host.Services);

        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        var who = await http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);

        // The backend-for-frontend rule, asserted as the absence it is: what the UI is told about the
        // operator is a name and two booleans, and nothing it could put in an Authorization header.
        Assert.True(who.GetProperty("signedIn").GetBoolean());
        Assert.Equal(["enabled", "name", "signedIn"],
            who.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>An empty JSON body, so that a POST to the MCP route is refused for the token and not the shape.</summary>
    private static StringContent EmptyJson() => new("{}", System.Text.Encoding.UTF8, "application/json");
}
