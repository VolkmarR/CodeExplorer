using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Drives the walking skeleton end to end: projects created through the operator endpoint become
///     reachable as MCP endpoints in the same process, and the route alone decides which project a
///     session is bound to.
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
        Directory.Delete(_dataDirectory, true);
    }

    [Fact]
    public async Task Mcp_client_lists_and_calls_the_tool_on_a_project_route()
    {
        await CreateProjectAsync("alpha", "Alpha Project");

        await using var client = await ConnectAsync("alpha");
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "which_project");

        string text = await CallWhichProjectAsync(client);
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
    public async Task Mcp_client_cannot_connect_to_an_unknown_slug()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = await ConnectAsync("nope");
        });
        Assert.Contains("404", error.ToString());
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

    [Fact]
    public async Task A_single_repository_project_refuses_a_second_repository()
    {
        using var http = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var created = await http.PostAsJsonAsync("/api/projects",
            new { slug = "solo", name = "Solo", singleRepository = true }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The slug sent with the first repository is ignored: a single-repository project heads no path
        // with one, so the system assigns it and the operator is never asked (ADR-0006).
        using var first = await http.PostAsJsonAsync("/api/projects/solo/repositories",
            new { slug = "ignored", url = "https://example.com/one.git", credential = (string?)null }, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var repositories = await http.GetFromJsonAsync<RepositoryResponse[]>("/api/projects/solo/repositories", ct);
        Assert.Equal("solo", Assert.Single(repositories!).Slug);

        using var second = await http.PostAsJsonAsync("/api/projects/solo/repositories",
            new { slug = "two", url = "https://example.com/two.git", credential = (string?)null }, ct);

        // Refused for good, not until something changes: the declaration cannot be edited, so the
        // message has to send the operator to a new project rather than to a setting.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<ErrorBody>(ct);
        Assert.Contains("single-repository project", error!.Error, StringComparison.Ordinal);
        Assert.Contains("Create another project", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_repository_is_off_unless_asked_for()
    {
        using var http = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // A request that predates ADR-0006 carries no flag, and must keep the shape it had: the field
        // decides how every file in the project is named.
        using var created = await http.PostAsJsonAsync("/api/projects", new { slug = "plain", name = "Plain" }, ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await created.Content.ReadFromJsonAsync<Project>(ct);
        Assert.False(project!.SingleRepository);
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
