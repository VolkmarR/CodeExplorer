using System.Net;
using System.Net.Http.Json;
using CodeExplorer.Index;
using CodeExplorer.Refresh;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A repository on the server's own disk — a path or a <c>file://</c> URL — is refused unless
///     <c>Control:AllowLocalRepositories</c> is true, and the default is off (GHSA-5373-pppr-q3q9).
///     Anyone who may add a repository could otherwise have the server clone any repository it can
///     read, other projects' local copies included, and serve it through MCP. The rest of the suite
///     runs with the setting on, because its fixtures are local repositories.
/// </summary>
public sealed class LocalRepositoryTests : IDisposable
{
    private const string Setting = "Control:AllowLocalRepositories";

    private readonly TestHost _host = new(SearchEngine.Substring, allowLocalRepositories: false);

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(@"C:\repo")]
    [InlineData("/srv/repo")]
    [InlineData("file:///srv/repo")]
    [InlineData("../fixtures/repo")]
    [InlineData(@"\\server\share\repo")]
    [InlineData("FILE:///srv/repo")]
    [InlineData(@"\\?\C:\repo")]
    [InlineData("C:repo")]
    public async Task A_local_repository_is_refused_and_the_answer_names_the_setting(string url)
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();

        using var response =
            await http.PostAsJsonAsync("/api/projects/alpha/repositories", new { slug = "main", url }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(Setting, await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(await http.GetFromJsonAsync<object[]>("/api/projects/alpha/repositories", Ct) ?? []);
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("http://example.invalid/repo.git")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/org/project/repo")]
    [InlineData("git://example.invalid/repo.git")]
    [InlineData("git@github.com:org/repo.git")]
    public async Task A_remote_repository_is_still_accepted(string url)
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();

        using var response =
            await http.PostAsJsonAsync("/api/projects/alpha/repositories", new { slug = "main", url }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    ///     A repository added while the setting was on must stop working once it is off, whether its
    ///     local copy already exists — the refresh would fetch — or not yet — it would clone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_local_repository_is_skipped_once_the_setting_is_off(bool clonedBefore)
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "main",
            host.CreateGitRepository("main", new Dictionary<string, string> { ["README.md"] = "hello" }));
        if (clonedBefore) await host.RefreshAsync("alpha");

        host.RestartWithoutLocalRepositories();
        using (var response = await host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await host.WaitForRefreshesAsync();

        var status = await host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        string error = Assert.IsType<string>(status.Error);
        Assert.Contains("'main'", error);
        Assert.Contains(Setting, error);
        // Refused before libgit2 was asked: a copy that was never made is not made now.
        if (!clonedBefore) Assert.False(Directory.Exists(host.ClonePath("alpha", "main")));
    }

    /// <summary>
    ///     A local copy left on disk from a local repository, under a repository now stored with a
    ///     remote URL, is fetched from that URL and not from the origin the folder still names. Fetching
    ///     the folder's origin would read the server's disk past the refusal.
    /// </summary>
    [Fact]
    public async Task A_leftover_local_copy_is_fetched_from_the_stored_url_and_not_its_own_origin()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "main",
            host.CreateGitRepository("main", new Dictionary<string, string> { ["README.md"] = "hello" }));
        await host.RefreshAsync("alpha");
        await host.ExecuteOnControlDatabaseAsync(
            "UPDATE repositories SET url = 'https://example.invalid/main.git' WHERE slug = 'main'");

        host.RestartWithoutLocalRepositories();
        using (var response = await host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await host.WaitForRefreshesAsync();

        var status = await host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.Contains("example.invalid", Assert.IsType<string>(status.Error));
    }
}
