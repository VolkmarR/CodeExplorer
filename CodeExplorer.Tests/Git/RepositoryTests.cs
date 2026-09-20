using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Repositories added through the operator API, and the local copy a refresh makes of them: a
///     shallow bare clone that nothing but the refresh reads (CONTEXT.md, Local copy). Fixtures are
///     local repositories built with LibGit2Sharp, so the suite never touches the network; ADR-0003
///     records that the local transport cannot serve a shallow clone, which is why the clone depth is
///     not asserted here. What the index built from a clone answers is asserted where the tools are
///     tested.
/// </summary>
public sealed class RepositoryTests : IDisposable
{
    private const string Secret = "pat-secret-token-3f9a";

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_refresh_clones_bare_and_the_clone_is_not_the_answer()
    {
        string source = _host.CreateGitRepository("source", new Dictionary<string, string>
        {
            ["README.md"] = "hello",
            ["src/Program.cs"] = "class P {}"
        });
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", source);

        // No clone before the refresh: a tool call must never make the server talk to a remote.
        string cloneDir = _host.ClonePath("alpha", "main");
        Assert.False(Directory.Exists(cloneDir));

        var summary = await _host.RefreshAsync("alpha");
        Assert.Equal(2, summary.Files);

        // Bare: the clone directory has objects but no working copy of README.md.
        Assert.True(Directory.Exists(Path.Combine(cloneDir, "objects")));
        Assert.False(File.Exists(Path.Combine(cloneDir, "README.md")));
    }

    [Fact]
    public async Task Clone_failure_is_an_actionable_message_that_never_carries_the_credential()
    {
        await _host.CreateProjectAsync("alpha");
        string missing = _host.ScratchFile("does-not-exist");
        await _host.AddRepositoryAsync("alpha", "broken", missing, Secret);

        // The only repository could not be read, so the refresh fails as a whole and its error is the
        // clone's message.
        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _host.WaitForRefreshesAsync();
        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        string error = Assert.IsType<string>(status.Error);

        Assert.Contains("broken", error);
        Assert.Contains("credential", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, error);
        Assert.DoesNotContain("   at ", error);
    }

    [Fact]
    public async Task Credential_is_write_only_through_the_api()
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();
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
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();
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

}
