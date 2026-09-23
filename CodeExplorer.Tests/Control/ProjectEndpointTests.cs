using System.Net;
using System.Net.Http.Json;
using CodeExplorer.Control;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Drives the walking skeleton end to end: projects created through the operator endpoint become
///     reachable as MCP endpoints in the same process, and the route alone decides which project a
///     session is bound to.
/// </summary>
public sealed class ProjectEndpointTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    /// <summary>
    ///     Binding a project reads the control database once and then remembers it (#149). Every
    ///     request under a project route and every MCP call asked "does this slug name a project", and
    ///     each one opened a control-database connection to find out.
    ///     Proved by taking the row away behind the server's back, through the server's own instance.
    ///     A second call that went back to the database would find no such project and refuse the
    ///     session — there is no way for this to pass except by not asking. Deleting the file stopped
    ///     proving it once the server held the file open for its whole life (#172): the instance
    ///     outlives the file, so a query would still have answered.
    ///     <c>which_project</c> is the call, because it is the one that reads the bound project and
    ///     nothing else. Its neighbours here legitimately read the control database again — a project's
    ///     page lists its repositories — and this is about the binding, not about them.
    /// </summary>
    [Fact]
    public async Task Binding_a_project_reads_the_control_database_once()
    {
        await _host.CreateProjectAsync("alpha", name: "Alpha Project");
        await using var client = await _host.ConnectAsync("alpha");
        Assert.Contains("Alpha Project", await WhichProjectAsync(client));

        await _host.ExecuteOnControlDatabaseAsync("DELETE FROM projects WHERE slug = 'alpha'");

        Assert.Contains("Alpha Project", await WhichProjectAsync(client));
    }

    /// <summary>
    ///     The other half of remembering: the writer that ends a project's life forgets it, so the very
    ///     next request is refused. A cache that outlived a delete would keep answering for a project
    ///     whose index and clones the same call had already removed.
    /// </summary>
    [Fact]
    public async Task A_deleted_project_is_refused_on_the_next_request()
    {
        await _host.CreateProjectAsync("alpha", name: "Alpha Project");
        using var http = _host.CreateClient();
        using (var bound = await http.GetAsync("/api/projects/alpha", Ct))
            Assert.Equal(HttpStatusCode.OK, bound.StatusCode);

        using (var deleted = await http.DeleteAsync("/api/projects/alpha", Ct))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var gone = await http.GetAsync("/api/projects/alpha", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Mcp_client_lists_and_calls_the_tool_on_a_project_route()
    {
        await _host.CreateProjectAsync("alpha", name: "Alpha Project");
        await using var client = await _host.ConnectAsync("alpha");

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Contains(tools, t => t.Name == "which_project");

        string text = await WhichProjectAsync(client);
        Assert.Contains("alpha", text);
        Assert.Contains("Alpha Project", text);
    }

    [Fact]
    public async Task Tool_answers_from_the_route_for_two_projects_in_one_process()
    {
        await _host.CreateProjectAsync("alpha", name: "Alpha Project");
        await _host.CreateProjectAsync("beta", name: "Beta Project");

        await using var alpha = await _host.ConnectAsync("alpha");
        await using var beta = await _host.ConnectAsync("beta");

        Assert.Contains("Alpha Project", await WhichProjectAsync(alpha));
        Assert.Contains("Beta Project", await WhichProjectAsync(beta));
    }

    [Fact]
    public async Task Unknown_slug_returns_404_with_an_error_body()
    {
        using var http = _host.CreateClient();
        using var response = await http.PostAsJsonAsync(
            "/projects/nope/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>(Ct);
        Assert.NotNull(body);
        Assert.Contains("nope", body.Error);
    }

    [Fact]
    public async Task Mcp_client_cannot_connect_to_an_unknown_slug()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = await _host.ConnectAsync("nope");
        });
        Assert.Contains("404", error.ToString());
    }

    [Fact]
    public async Task Creating_a_project_rejects_bad_slugs_and_duplicates()
    {
        using var http = _host.CreateClient();

        using var bad = await http.PostAsJsonAsync("/api/projects", new { slug = "Not Valid", name = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var first = await http.PostAsJsonAsync("/api/projects", new { slug = "alpha", name = "Alpha" }, Ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var dup = await http.PostAsJsonAsync("/api/projects", new { slug = "alpha", name = "Again" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task A_single_repository_project_refuses_a_second_repository()
    {
        await _host.CreateProjectAsync("solo", true);
        using var http = _host.CreateClient();

        // The slug sent with the first repository is ignored: a single-repository project heads no path
        // with one, so the system assigns it and the operator is never asked (ADR-0006).
        await _host.AddRepositoryAsync("solo", "ignored", "https://example.com/one.git");
        var repositories = await http.GetFromJsonAsync<RepositoryResponse[]>("/api/projects/solo/repositories", Ct);
        Assert.Equal("solo", Assert.Single(repositories!).Slug);

        using var second = await http.PostAsJsonAsync("/api/projects/solo/repositories",
            new { slug = "two", url = "https://example.com/two.git", credential = (string?)null }, Ct);

        // Refused for good, not until something changes: the declaration cannot be edited, so the
        // message has to send the operator to a new project rather than to a setting.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<ErrorBody>(Ct);
        Assert.Contains("single-repository project", error!.Error, StringComparison.Ordinal);
        Assert.Contains("Create another project", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_repository_is_off_unless_asked_for()
    {
        using var http = _host.CreateClient();

        // A request that predates ADR-0006 carries no flag, and must keep the shape it had: the field
        // decides how every file in the project is named.
        using var created = await http.PostAsJsonAsync("/api/projects", new { slug = "plain", name = "Plain" }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await created.Content.ReadFromJsonAsync<Project>(Ct);
        Assert.False(project!.SingleRepository);
    }

    private static Task<string> WhichProjectAsync(McpClient client) =>
        TestHost.CallAsync(client, "which_project", new Dictionary<string, object?>());

    private sealed record ErrorBody(string Error);
}
