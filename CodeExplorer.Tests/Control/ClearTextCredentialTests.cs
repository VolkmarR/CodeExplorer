using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using CodeExplorer.Index;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A credential reaches the remote as HTTP basic auth, so over <c>http://</c> it crosses the network in
///     clear text to anyone on the path (GHSA-4f8q-c6jj-fr44). It may only be stored for a URL whose
///     transport is encrypted, and one stored before that rule is never sent.
/// </summary>
public sealed class ClearTextCredentialTests : IDisposable
{
    private const string Secret = "pat-secret-token-7c1e";
    private const string Refusal = "A credential is only sent over https or ssh";

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    // The URL shapes are RepositoryUrlTests'; these are the ones that also pass Classify on the way in.
    [InlineData("http://example.invalid/repo.git")]
    [InlineData("git://example.invalid/repo.git")]
    [InlineData("http://example invalid/repo.git")]
    public async Task A_credential_for_an_unencrypted_url_is_refused(string url)
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();

        using var response = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "main", url, credential = Secret }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains(Refusal, body);
        Assert.DoesNotContain(Secret, body);
        Assert.Empty(await http.GetFromJsonAsync<object[]>("/api/projects/alpha/repositories", Ct) ?? []);
    }

    [Theory]
    [InlineData("http://example.invalid/repo.git", null)]
    [InlineData("http://example.invalid/repo.git", "")]
    [InlineData("https://example.invalid/repo.git", Secret)]
    [InlineData("ssh://git@example.invalid/repo.git", Secret)]
    [InlineData("git@example.invalid:org/repo.git", Secret)]
    public async Task An_encrypted_url_or_no_credential_is_still_accepted(string url, string? credential)
    {
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", url, credential);
    }

    /// <summary>
    ///     A repository stored with a credential before the API refused the pair is skipped with the same
    ///     sentence, and libgit2 is never asked: the remote is a loopback listener that would answer every
    ///     request with a basic-auth challenge, and it is never so much as connected to. Covers the fetch
    ///     path (a local copy exists) and the clone path (none does yet).
    ///     The challenge is what makes the red run end: without the fix libgit2 replays the credential
    ///     against it and gives up, where a silent listener would hold the refresh for the stall limit.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_http_repository_with_a_credential_is_skipped_and_never_contacted(bool clonedBefore)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int connections = 0;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var socket = await listener.AcceptSocketAsync(Ct);
                    Interlocked.Increment(ref connections);
                    await socket.SendAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"git\"\r\n"
                        + "Content-Length: 0\r\nConnection: close\r\n\r\n"), Ct);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or OperationCanceledException)
            {
                // Safe to swallow: disposing the listener at the end of the test is what ends this loop.
            }
        }, Ct);
        string remote = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/remote.git";

        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "healthy",
            _host.CreateGitRepository("healthy", new Dictionary<string, string> { ["a.cs"] = "class A {}" }));
        // A local repository, whose transport never asks for the credential, makes a copy to fetch into.
        await _host.AddRepositoryAsync("alpha", "plain", clonedBefore
            ? _host.CreateGitRepository("plain", new Dictionary<string, string> { ["b.cs"] = "class B {}" })
            : "https://example.invalid/plain.git", Secret);
        if (clonedBefore) await _host.RefreshAsync("alpha");
        // Stored the way a server that predates the refusal stored it: the credential beside an http URL.
        // Safe to inline: the value is the test's own loopback URL, built from a port number.
        await _host.ExecuteOnControlDatabaseAsync($"UPDATE repositories SET url = '{remote}' WHERE slug = 'plain'");

        var summary = await _host.RefreshAsync("alpha");
        listener.Stop();
        await serving;

        string skipped = Assert.Single(summary.Skipped);
        Assert.Contains("'plain'", skipped);
        Assert.Contains(Refusal, skipped);
        Assert.DoesNotContain(Secret, skipped);
        Assert.Equal(0, connections);
        // Refused before libgit2 was asked: a copy that was never made is not made now.
        if (!clonedBefore) Assert.False(Directory.Exists(_host.ClonePath("alpha", "plain")));
    }
}
