using System.Net;
using System.Net.Http.Json;
using CodeExplorer.Index;
using CodeExplorer.Operator;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Adding a repository through the operator API: what the request is checked for, and the
///     credential it may carry, which goes in and never comes back out. Nothing here is refreshed, so
///     the URLs name remotes that are never contacted.
/// </summary>
public sealed class RepositoryEndpointTests : IDisposable
{
    private const string Secret = "pat-secret-token-3f9a";

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Write-only, on every read that lists the repository: the answer to the request that stored the
    ///     credential, and the project page, which lists the repositories and says whether one is set
    ///     and nothing more.
    /// </summary>
    [Fact]
    public async Task A_credential_is_reported_as_set_and_never_returned()
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();

        using var created = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "main", url = "https://example.invalid/repo.git", credential = Secret }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string createdBody = await created.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(Secret, createdBody);
        Assert.Contains("\"hasCredential\":true", createdBody);
        await _host.AddRepositoryAsync("alpha", "plain", "https://example.invalid/plain.git");

        string json = await http.GetStringAsync("/api/projects/alpha", Ct);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        var detail = await _host.GetJsonAsync<ProjectDetail>("/api/projects/alpha");
        Assert.True(Assert.Single(detail.Repositories, r => r.Slug == "main").HasCredential);
        Assert.False(Assert.Single(detail.Repositories, r => r.Slug == "plain").HasCredential);
    }

    [Fact]
    public async Task Adding_a_repository_validates_project_slug_url_and_duplicates()
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();
        var body = new { slug = "main", url = "https://example.invalid/repo.git" };

        using var noProject = await http.PostAsJsonAsync("/api/projects/nope/repositories", body, Ct);
        Assert.Equal(HttpStatusCode.NotFound, noProject.StatusCode);

        using var badSlug = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "Bad Slug", body.url }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badSlug.StatusCode);

        // A token in the URL would land on disk in the clone's remote config; the API refuses it.
        using var userInfo = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "main", url = $"https://user:{Secret}@example.invalid/repo.git" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, userInfo.StatusCode);
        Assert.DoesNotContain(Secret, await userInfo.Content.ReadAsStringAsync(Ct));

        using var first = await http.PostAsJsonAsync("/api/projects/alpha/repositories", body, Ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var dup = await http.PostAsJsonAsync("/api/projects/alpha/repositories", body, Ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }
}
