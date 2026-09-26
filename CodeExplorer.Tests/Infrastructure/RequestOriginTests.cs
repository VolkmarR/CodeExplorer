using System.Net;
using System.Text;
using CodeExplorer.Index;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Which requests a server answers by where they come from (GHSA-qxhv-3r9w-q8h4). A page the
///     operator visits can rebind its own name to 127.0.0.1 and so become same-origin with an
///     unauthenticated server; the Host check is what stops that. A page that merely posts across
///     origins to localhost is stopped by the Origin check.
/// </summary>
public sealed class RequestOriginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpStatusCode> GetAsync(TestHost host, string path, string? hostHeader = null,
        string? origin = null) =>
        SendAsync(host, new HttpRequestMessage(HttpMethod.Get, path), hostHeader, origin);

    private static Task<HttpStatusCode> PostAsync(TestHost host, string path, string origin, string json = "{}") =>
        SendAsync(host,
            new HttpRequestMessage(HttpMethod.Post, path)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") },
            origin: origin);

    private static async Task<HttpStatusCode> SendAsync(TestHost host, HttpRequestMessage message,
        string? hostHeader = null, string? origin = null)
    {
        using var http = host.CreateClient();
        using var request = message;
        if (hostHeader is not null) request.Headers.Host = hostHeader;
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task With_no_tenant_a_rebound_host_name_is_refused()
    {
        using var host = new TestHost(SearchEngine.Substring);

        Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(host, "/api/projects", "attacker.example"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, "/api/projects", "localhost:5000"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, "/api/projects", "127.0.0.1:5000"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, "/api/projects", "[::1]:5000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("code.example")]
    public async Task With_no_tenant_a_request_without_a_host_is_refused(string? allowedHosts)
    {
        // The framework's filter serves a missing Host by default, which names no allowed host either.
        using var host = new TestHost(SearchEngine.Substring, allowedHosts: allowedHosts);

        Assert.Equal(HttpStatusCode.BadRequest, await host.GetWithoutHostAsync("/api/projects"));
    }

    [Fact]
    public async Task A_configured_allowed_hosts_replaces_the_loopback_default()
    {
        using var host = new TestHost(SearchEngine.Substring, allowedHosts: "code.example");

        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, "/api/projects", "code.example"));
        Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(host, "/api/projects", "localhost"));
    }

    [Fact]
    public async Task With_a_tenant_the_host_is_left_to_the_fallback_policy()
    {
        // A rebound page is one more anonymous caller there, which the sign-in check already refuses,
        // and a deployed server is reached under names nobody has written into its configuration.
        using var host = new TestHost(SearchEngine.Substring, authenticated: true);

        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, "/api/auth/me", "code.example"));
    }

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/API/projects")]
    [InlineData("/projects/open/mcp")]
    [InlineData("/projects/open/MCP/")]
    public async Task A_foreign_origin_is_refused_on_the_api_and_the_mcp_endpoint(string path)
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("open");

        // POST, because it is the verb both endpoints answer and the one a cross-origin page sends
        // without a preflight.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(host, path, "http://attacker.example"));
        // Same host, other port: the Vite dev server, which is why its proxy removes the header.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(host, path, "http://localhost:5173"));
        // What a sandboxed frame or a file sends.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(host, path, "null"));
    }

    [Fact]
    public async Task A_cross_origin_write_changes_nothing()
    {
        using var host = new TestHost(SearchEngine.Substring);

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(host, "/api/projects", "http://attacker.example",
            """{"slug":"planted","name":"Planted"}"""));

        using var http = host.CreateClient();
        Assert.DoesNotContain("planted", await http.GetStringAsync("/api/projects", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_origin_or_this_servers_own_is_served()
    {
        using var host = new TestHost(SearchEngine.Substring);

        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, "/api/projects"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(host, "/api/projects", origin: "http://localhost"));
        Assert.Equal(HttpStatusCode.OK,
            await GetAsync(host, "/api/projects", "localhost:5000", "http://localhost:5000"));
    }
}
