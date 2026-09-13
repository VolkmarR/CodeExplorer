using System.Net;
using System.Net.Http.Json;
using LibGit2Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     One in-process server with its own data directory and a pinned search engine, plus the fixture
///     steps every index-backed test repeats: a git repository with one commit, a project, its
///     repositories, a build, and an MCP client bound to the project's route.
/// </summary>
public sealed class TestHost : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    public TestHost(SearchEngine engine)
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:DataDirectory", Path.Combine(_root, "data"));
            builder.UseSetting("Index:SearchEngine", engine.ToString());
        });
    }

    public WebApplicationFactory<Program> Factory { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Factory.Dispose();
        if (!Directory.Exists(_root)) return;
        // libgit2 marks pack files read-only; Delete(recursive) refuses those unless cleared first.
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(_root, true);
    }

    /// <summary>Builds a non-bare repository with one commit holding the given files and returns its path.</summary>
    public string CreateGitRepository(string name, Dictionary<string, string> files)
    {
        string path = Path.Combine(_root, "fixtures", name);
        Repository.Init(path);
        using var repo = new Repository(path);
        foreach ((string relative, string content) in files)
        {
            string full = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Commands.Stage(repo, relative);
        }

        var author = new Signature("Test", "test@example.invalid", DateTimeOffset.UnixEpoch);
        repo.Commit("fixture", author, author);
        return path;
    }

    /// <summary>Where <see cref="CreateGitRepository" /> put the fixture with this name.</summary>
    public string FixturePath(string name) => Path.Combine(_root, "fixtures", name);

    /// <summary>What the host was pointed at, for a test asserting which files a deletion left behind.</summary>
    public string DataDirectory => Path.Combine(_root, "data");

    public async Task CreateProjectAsync(string slug, bool singleRepository = false)
    {
        using var http = Factory.CreateClient();
        using var response =
            await http.PostAsJsonAsync("/api/projects", new { slug, name = slug, singleRepository }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public async Task AddRepositoryAsync(string project, string slug, string url, string? credential = null)
    {
        using var http = Factory.CreateClient();
        using var response =
            await http.PostAsJsonAsync($"/api/projects/{project}/repositories", new { slug, url, credential }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public async Task<IndexSummary> IndexAsync(string project)
    {
        using var http = Factory.CreateClient();
        using var response = await http.PostAsync($"/api/projects/{project}/index", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<IndexSummary>(Ct);
        Assert.NotNull(summary);
        return summary;
    }

    /// <summary>A project of the given repositories (slug to files), created, added and indexed.</summary>
    public async Task IndexedProjectAsync(string project, Dictionary<string, Dictionary<string, string>> repositories,
        bool singleRepository = false)
    {
        await CreateProjectAsync(project, singleRepository);
        foreach ((string slug, var files) in repositories)
            await AddRepositoryAsync(project, slug, CreateGitRepository(slug, files));
        await IndexAsync(project);
    }

    public async Task<McpClient> ConnectAsync(string slug)
    {
        var http = Factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, $"/projects/{slug}/mcp") },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>Calls a tool and returns its single text block.</summary>
    public static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        var block = Assert.Single(result.Content);
        return Assert.IsType<TextContentBlock>(block).Text;
    }
}
