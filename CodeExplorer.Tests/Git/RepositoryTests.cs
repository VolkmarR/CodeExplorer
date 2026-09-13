using System.Net;
using System.Net.Http.Json;
using LibGit2Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Repositories added through the operator API are cloned bare on first use and listed by
///     <c>list_tree</c>. Fixtures are local repositories built with LibGit2Sharp, so the suite never
///     touches the network; ADR-0003 records that the local transport cannot serve a shallow clone,
///     which is why the clone depth is not asserted here.
/// </summary>
public sealed class RepositoryTests : IDisposable
{
    private const string Secret = "pat-secret-token-3f9a";

    private readonly WebApplicationFactory<Program> _factory;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    public RepositoryTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Storage:DataDirectory", Path.Combine(_root, "data")));
    }

    public void Dispose()
    {
        _factory.Dispose();
        // A test that fails before its first request never creates the folder; do not mask that failure.
        if (!Directory.Exists(_root)) return;
        // libgit2 marks pack files read-only; Delete(recursive) refuses those unless cleared first.
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task List_tree_clones_on_first_use_and_lists_to_the_given_depth()
    {
        string source = CreateGitRepository("source", new Dictionary<string, string>
        {
            ["README.md"] = "hello",
            ["src/Program.cs"] = "class P {}",
            ["src/Lib/Util.cs"] = "class U {}",
            ["docs/guide.md"] = "guide"
        });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "main", source);

        await using var client = await ConnectAsync("alpha");

        string shallow = await ListTreeAsync(client, "main", 1);
        Assert.Contains("README.md", shallow);
        Assert.Contains("src/", shallow);
        Assert.Contains("docs/", shallow);
        Assert.DoesNotContain("Program.cs", shallow);

        string deep = await ListTreeAsync(client, "main/src", 2);
        Assert.Contains("Program.cs", deep);
        Assert.Contains("Lib/", deep);
        Assert.Contains("Lib/Util.cs", deep);
        Assert.DoesNotContain("README.md", deep);

        // Clone is bare: the clone directory has objects but no working copy of README.md.
        string cloneDir = Path.Combine(_root, "data", "clones", "alpha", "main.git");
        Assert.True(Directory.Exists(Path.Combine(cloneDir, "objects")));
        Assert.False(File.Exists(Path.Combine(cloneDir, "README.md")));
    }

    [Fact]
    public async Task Project_root_lists_every_repository_by_slug()
    {
        string one = CreateGitRepository("one", new Dictionary<string, string> { ["a.txt"] = "a" });
        string two = CreateGitRepository("two", new Dictionary<string, string> { ["b.txt"] = "b" });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "first", one);
        await AddRepositoryAsync("alpha", "second", two);

        await using var client = await ConnectAsync("alpha");
        string root = await ListTreeAsync(client, "", 2);

        Assert.Contains("first/", root);
        Assert.Contains("first/a.txt", root);
        Assert.Contains("second/b.txt", root);
    }

    [Fact]
    public async Task Lfs_repository_is_refused_naming_lfs()
    {
        string source = CreateGitRepository("lfs", new Dictionary<string, string>
        {
            [".gitattributes"] = "*.bin filter=lfs diff=lfs merge=lfs -text\n",
            ["model.bin"] = "version https://git-lfs.github.com/spec/v1"
        });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "main", source);

        await using var client = await ConnectAsync("alpha");
        string text = await ListTreeAsync(client, "main", 1);

        Assert.Contains("LFS", text);
        Assert.DoesNotContain("model.bin", text);
    }

    [Fact]
    public async Task Clone_failure_is_an_actionable_message_that_never_carries_the_credential()
    {
        await CreateProjectAsync("alpha");
        string missing = Path.Combine(_root, "does-not-exist");
        await AddRepositoryAsync("alpha", "broken", missing, Secret);

        await using var client = await ConnectAsync("alpha");
        var error = await Assert.ThrowsAsync<McpException>(() => ListTreeAsync(client, "broken", 1));

        Assert.Contains("broken", error.Message);
        Assert.Contains("credential", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, error.Message);
        Assert.DoesNotContain("   at ", error.Message);
    }

    [Fact]
    public async Task Unknown_repository_and_unknown_path_are_answers_not_errors()
    {
        string source = CreateGitRepository("source", new Dictionary<string, string> { ["a.txt"] = "a" });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "main", source);

        await using var client = await ConnectAsync("alpha");

        string noRepo = await ListTreeAsync(client, "nope", 1);
        Assert.Contains("nope", noRepo);
        Assert.Contains("main", noRepo);

        string noPath = await ListTreeAsync(client, "main/missing/dir", 1);
        Assert.Contains("missing/dir", noPath);
    }

    [Fact]
    public async Task Credential_is_write_only_through_the_api()
    {
        await CreateProjectAsync("alpha");
        using var http = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var created = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "main", url = "https://example.invalid/repo.git", credential = Secret }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string createdBody = await created.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(Secret, createdBody);
        Assert.Contains("\"hasCredential\":true", createdBody);

        string listBody = await http.GetStringAsync("/api/projects/alpha/repositories", ct);
        Assert.DoesNotContain(Secret, listBody);
        Assert.Contains("main", listBody);
    }

    [Fact]
    public async Task Adding_a_repository_validates_project_slug_url_and_duplicates()
    {
        await CreateProjectAsync("alpha");
        using var http = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var body = new { slug = "main", url = "https://example.invalid/repo.git" };

        using var noProject = await http.PostAsJsonAsync("/api/projects/nope/repositories", body, ct);
        Assert.Equal(HttpStatusCode.NotFound, noProject.StatusCode);

        using var badSlug = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "Bad Slug", body.url }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, badSlug.StatusCode);

        // A token in the URL would land on disk in the clone's remote config; the API refuses it.
        using var userInfo = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "main", url = $"https://user:{Secret}@example.invalid/repo.git" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, userInfo.StatusCode);
        Assert.DoesNotContain(Secret, await userInfo.Content.ReadAsStringAsync(ct));

        using var first = await http.PostAsJsonAsync("/api/projects/alpha/repositories", body, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var dup = await http.PostAsJsonAsync("/api/projects/alpha/repositories", body, ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", RepositoryUrlKind.Remote)]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/org/project/repo", RepositoryUrlKind.Remote)]
    [InlineData("git@github.com:org/repo.git", RepositoryUrlKind.Remote)]
    [InlineData(@"C:\mirrors\repo.git", RepositoryUrlKind.Local)]
    [InlineData("file:///srv/mirrors/repo.git", RepositoryUrlKind.Local)]
    [InlineData("../fixtures/repo", RepositoryUrlKind.Local)]
    [InlineData("https://user:token@github.com/org/repo.git", RepositoryUrlKind.Invalid)]
    [InlineData("ssh://git:token@host/repo", RepositoryUrlKind.Invalid)]
    [InlineData("user:token@host:org/repo.git", RepositoryUrlKind.Invalid)]
    [InlineData("ftp://host/repo", RepositoryUrlKind.Invalid)]
    [InlineData("", RepositoryUrlKind.Invalid)]
    public void Repository_urls_are_classified_and_secrets_in_them_refused(string url, RepositoryUrlKind expected) =>
        Assert.Equal(expected, RepositoryUrl.Classify(url));

    /// <summary>Builds a non-bare repository with one commit holding the given files.</summary>
    private string CreateGitRepository(string name, Dictionary<string, string> files)
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

    private async Task CreateProjectAsync(string slug)
    {
        using var http = _factory.CreateClient();
        using var response = await http.PostAsJsonAsync(
            "/api/projects", new { slug, name = slug }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task AddRepositoryAsync(string project, string slug, string url, string? credential = null)
    {
        using var http = _factory.CreateClient();
        using var response = await http.PostAsJsonAsync(
            $"/api/projects/{project}/repositories", new { slug, url, credential },
            TestContext.Current.CancellationToken);
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

    private static async Task<string> ListTreeAsync(McpClient client, string path, int depth)
    {
        var result = await client.CallToolAsync(
            "list_tree",
            new Dictionary<string, object?> { ["path"] = path, ["depth"] = depth },
            cancellationToken: TestContext.Current.CancellationToken);
        var block = Assert.Single(result.Content);
        string text = Assert.IsType<TextContentBlock>(block).Text;
        // The SDK reports a handler's McpException as an error result rather than throwing on the
        // client, so re-raise it here to keep the assertion sites readable.
        if (result.IsError == true) throw new McpException(text);
        return text;
    }
}
