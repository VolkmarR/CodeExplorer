using System.Net;
using System.Net.Http.Json;
using CodeExplorer.Index;
using Microsoft.Extensions.Logging;
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

    // A remote on the server itself: over the network, but to the same machine as a local path.
    private const string LoopbackHttp = "http://localhost/repo.git";
    private const string LoopbackSsh = "ssh://git@[::1]/repo.git";
    private const string LoopbackScp = "git@127.0.0.1:org/repo.git";

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
    [InlineData(LoopbackHttp)]
    [InlineData("http://localhost./repo.git")]
    [InlineData("https://127.0.0.1/repo.git")]
    [InlineData("http://2130706433/repo.git")]
    [InlineData("http://0x7f000001/repo.git")]
    [InlineData("http://[::1]/repo.git")]
    [InlineData("http://[::ffff:127.0.0.1]/repo.git")]
    [InlineData(LoopbackSsh)]
    [InlineData(LoopbackScp)]
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
    [InlineData("https://localhost.example.com/repo.git")]
    [InlineData("git@127.example.com:org/repo.git")]
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
        using var host = await LocalRepositoryHostAsync(clonedBefore);

        host.RestartWithoutLocalRepositories();
        string error = await host.FailedRefreshErrorAsync("alpha");

        Assert.Contains("'main'", error);
        Assert.Contains(Setting, error);
        // Refused before libgit2 was asked: a copy that was never made is not made now.
        if (!clonedBefore) Assert.False(Directory.Exists(host.ClonePath("alpha", "main")));
    }

    [Theory]
    [InlineData(LoopbackHttp)]
    [InlineData(LoopbackSsh)]
    [InlineData(LoopbackScp)]
    public async Task A_loopback_remote_is_accepted_where_local_repositories_are(string url)
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        using var http = host.CreateClient();

        using var response =
            await http.PostAsJsonAsync("/api/projects/alpha/repositories", new { slug = "main", url }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Refused on the stored URL before any transfer, as a stored local path is.</summary>
    [Fact]
    public async Task A_stored_loopback_remote_is_skipped_once_the_setting_is_off()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "main", LoopbackHttp);

        host.RestartWithoutLocalRepositories();
        string error = await host.FailedRefreshErrorAsync("alpha");

        Assert.Contains("'main' was not read. It is a local path, a file URL or a remote on a loopback host", error);
        Assert.Contains(Setting, error);
    }

    /// <summary>
    ///     A local copy left on disk from a local repository, under a repository now stored with a
    ///     remote URL, is not fetched from the origin it still names: that would read the server's disk
    ///     past the refusal. It is cloned over from the stored URL instead. A copy whose origin is the
    ///     stored URL is fetched as before, which the refresh ahead of the change proves.
    /// </summary>
    [Fact]
    public async Task A_leftover_local_copy_is_cloned_over_from_the_stored_url()
    {
        using var host = await LocalRepositoryHostAsync(refreshed: true);
        await host.RefreshAsync("alpha");
        await host.ExecuteOnControlDatabaseAsync(
            "UPDATE repositories SET url = 'https://example.invalid/main.git' WHERE slug = 'main'");

        host.RestartWithoutLocalRepositories();
        string error = await host.FailedRefreshErrorAsync("alpha");

        Assert.Contains("Cloning repository 'main' from 'https://example.invalid/main.git'", error);
        host.Logs.Only(LogLevel.Warning, "is cloned over");
        Assert.False(Directory.Exists(host.ClonePath("alpha", "main")));
    }

    /// <summary>A host that allows local repositories, with project alpha holding one of them as 'main'.</summary>
    private static async Task<TestHost> LocalRepositoryHostAsync(bool refreshed)
    {
        var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "main",
            host.CreateGitRepository("main", new Dictionary<string, string> { ["README.md"] = "hello" }));
        if (refreshed) await host.RefreshAsync("alpha");
        return host;
    }
}
