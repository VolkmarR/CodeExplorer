using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
/// Drives the walking skeleton end to end: projects created through the operator endpoint become
/// reachable as MCP endpoints in the same process, and the route alone decides which project a
/// session is bound to.
/// </summary>
public sealed class ProjectEndpointTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    private readonly WebApplicationFactory<Program> _factory;

    public ProjectEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Storage:DataDirectory", _dataDirectory));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public async Task Mcp_client_lists_and_calls_the_tool_on_a_project_route()
    {
        await CreateProjectAsync("alpha", "Alpha Project");

        await using var client = await ConnectAsync("alpha");
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "which_project");

        var text = await CallWhichProjectAsync(client);
        Assert.Contains("alpha", text);
        Assert.Contains("Alpha Project", text);
    }

    [Fact]
    public async Task Tool_answers_from_the_route_for_two_projects_in_one_process()
    {
        await CreateProjectAsync("alpha", "Alpha Project");
        await CreateProjectAsync("beta", "Beta Project");

        await using var alpha = await ConnectAsync("alpha");
        await using var beta = await ConnectAsync("beta");

        Assert.Contains("Alpha Project", await CallWhichProjectAsync(alpha));
        Assert.Contains("Beta Project", await CallWhichProjectAsync(beta));
    }

    [Fact]
    public async Task Unknown_slug_returns_404_with_an_error_body()
    {
        using var http = _factory.CreateClient();
        using var response = await http.PostAsJsonAsync(
            "/projects/nope/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Contains("nope", body.Error);
    }

    [Fact]
    public async Task Creating_a_project_rejects_bad_slugs_and_duplicates()
    {
        using var http = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var bad = await http.PostAsJsonAsync("/api/projects", new { slug = "Not Valid", name = "x" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var first = await http.PostAsJsonAsync("/api/projects", new { slug = "alpha", name = "Alpha" }, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var dup = await http.PostAsJsonAsync("/api/projects", new { slug = "alpha", name = "Again" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    private async Task CreateProjectAsync(string slug, string name)
    {
        using var http = _factory.CreateClient();
        using var response = await http.PostAsJsonAsync(
            "/api/projects", new { slug, name }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<McpClient> ConnectAsync(string slug)
    {
        var http = _factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, $"/projects/{slug}/mcp") },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<string> CallWhichProjectAsync(McpClient client)
    {
        var result = await client.CallToolAsync(
            "which_project", cancellationToken: TestContext.Current.CancellationToken);
        var block = Assert.Single(result.Content);
        return Assert.IsType<TextContentBlock>(block).Text;
    }

    private sealed record ErrorBody(string Error);
}
