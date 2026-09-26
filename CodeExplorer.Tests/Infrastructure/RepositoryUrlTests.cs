using CodeExplorer.Infrastructure;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What the API accepts as a repository URL, and what it refuses because it carries a secret. In
///     <c>Infrastructure/</c> with the classifier, which both the API and the clone read to decide
///     whether a URL is on the server's own disk (GHSA-5373-pppr-q3q9).
/// </summary>
public sealed class RepositoryUrlTests
{
    [Theory]
    [InlineData("https://github.com/org/repo.git", RepositoryUrlKind.Remote)]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/org/project/repo", RepositoryUrlKind.Remote)]
    [InlineData("git@github.com:org/repo.git", RepositoryUrlKind.Remote)]
    [InlineData(@"C:\mirrors\repo.git", RepositoryUrlKind.Local)]
    [InlineData("file:///srv/mirrors/repo.git", RepositoryUrlKind.Local)]
    [InlineData("../fixtures/repo", RepositoryUrlKind.Local)]
    // Windows path shapes that hold a colon and could pass for scp-style host:path. libgit2 on Windows
    // opens any URL naming an existing directory through its local transport before it tries ssh, so
    // each of these read as remote would be a way past Control:AllowLocalRepositories.
    [InlineData(@"\\?\C:\mirrors\repo.git", RepositoryUrlKind.Local)]
    [InlineData(@"\\.\C:\mirrors\repo.git", RepositoryUrlKind.Local)]
    [InlineData(@"\\server\share\repo.git", RepositoryUrlKind.Local)]
    [InlineData("C:", RepositoryUrlKind.Local)]
    [InlineData("C:repo", RepositoryUrlKind.Local)]
    [InlineData(@"C:..\repo", RepositoryUrlKind.Local)]
    [InlineData(@" C:\mirrors\repo.git", RepositoryUrlKind.Local)]
    [InlineData(@"mirrors\sub:dir", RepositoryUrlKind.Local)]
    [InlineData("https://user:token@github.com/org/repo.git", RepositoryUrlKind.Invalid)]
    [InlineData("ssh://git:token@host/repo", RepositoryUrlKind.Invalid)]
    [InlineData("user:token@host:org/repo.git", RepositoryUrlKind.Invalid)]
    [InlineData("ftp://host/repo", RepositoryUrlKind.Invalid)]
    [InlineData("", RepositoryUrlKind.Invalid)]
    public void Repository_urls_are_classified_and_secrets_in_them_refused(string url, RepositoryUrlKind expected) =>
        Assert.Equal(expected, RepositoryUrl.Classify(url));

    /// <summary>
    ///     A remote whose host is loopback reaches the server itself over the network, so it counts as
    ///     local, in every spelling of the address and every URL form libgit2 reads.
    /// </summary>
    [Theory]
    [InlineData("http://localhost/repo.git")]
    [InlineData("https://LOCALHOST:8443/repo.git")]
    [InlineData("http://localhost./repo.git")]
    [InlineData("http://127.0.0.1/repo.git")]
    [InlineData("http://127.255.0.9:3000/repo.git")]
    [InlineData("http://127.1/repo.git")]
    [InlineData("http://2130706433/repo.git")]
    [InlineData("http://0x7f000001/repo.git")]
    [InlineData("http://127.0.0.1./repo.git")]
    [InlineData("http://[::1]/repo.git")]
    [InlineData("http://[0:0:0:0:0:0:0:1]/repo.git")]
    [InlineData("http://[::ffff:127.0.0.1]/repo.git")]
    [InlineData("ssh://git@localhost/repo.git")]
    [InlineData("ssh://git@127.0.0.1:2222/repo.git")]
    [InlineData("ssh://git@2130706433/repo.git")]
    [InlineData("ssh://git@0x7f000001/repo.git")]
    [InlineData("ssh://git@[::1]/repo.git")]
    [InlineData("ssh://git@[::ffff:127.0.0.1]/repo.git")]
    [InlineData("git://localhost/repo.git")]
    [InlineData("git@localhost:org/repo.git")]
    [InlineData("git@localhost.:org/repo.git")]
    [InlineData("127.0.0.1:org/repo.git")]
    [InlineData("git@127.0.0.1:org/repo.git")]
    [InlineData("git@2130706433:org/repo.git")]
    [InlineData("git@0x7f000001:org/repo.git")]
    [InlineData("http://ｌｏｃａｌｈｏｓｔ/repo.git")]
    public void A_loopback_remote_is_local(string url) =>
        Assert.Equal(RepositoryUrlKind.Local, RepositoryUrl.Classify(url));

    /// <summary>
    ///     .NET cannot parse these, and read as scp-style they would name the host "http" and pass as
    ///     remote, while libgit2 still reads them as URLs, and may decode them to a loopback host.
    /// </summary>
    [Theory]
    [InlineData("http://%6cocalhost/repo.git")]
    [InlineData("http://127.0.0.1%2e/repo.git")]
    [InlineData("http://example invalid/repo.git")]
    public void A_url_dotnet_cannot_parse_is_refused(string url) =>
        Assert.Equal(RepositoryUrlKind.Invalid, RepositoryUrl.Classify(url));

    [Theory]
    [InlineData("https://ｅｘａｍｐｌｅ.com/repo.git")]
    public void A_fullwidth_host_that_is_not_loopback_is_remote(string url) =>
        Assert.Equal(RepositoryUrlKind.Remote, RepositoryUrl.Classify(url));

    [Theory]
    [InlineData("https://localhost.example.com/repo.git")]
    [InlineData("https://mylocalhost/repo.git")]
    [InlineData("https://127.example.com/repo.git")]
    [InlineData("https://host127/repo.git")]
    [InlineData("ssh://git@localhost-mirror/repo.git")]
    [InlineData("git@localhost.example.com:org/repo.git")]
    [InlineData("git@127.example.com:org/repo.git")]
    [InlineData("http://128.0.0.1/repo.git")]
    [InlineData("http://[::2]/repo.git")]
    public void A_host_that_only_looks_like_loopback_is_remote(string url) =>
        Assert.Equal(RepositoryUrlKind.Remote, RepositoryUrl.Classify(url));

    /// <summary>A credential beside these would be sent unencrypted, or kept for a transport that has none (GHSA-4f8q-c6jj-fr44).</summary>
    [Theory]
    [InlineData("http://example.invalid/repo.git", true)]
    [InlineData("HTTP://example.invalid/repo.git", true)]
    [InlineData(" http://example.invalid/repo.git ", true)]
    [InlineData("http:example.invalid/repo.git", true)]
    // Not a URI .NET can parse, and read as scp-style by Classify, which libgit2 does not agree with.
    [InlineData("http://example invalid/repo.git", true)]
    [InlineData("git://example.invalid/repo.git", true)]
    [InlineData("GIT://example.invalid/repo.git", true)]
    [InlineData("https://example.invalid/repo.git", false)]
    [InlineData("ssh://git@example.invalid/repo.git", false)]
    [InlineData("git@example.invalid:org/repo.git", false)]
    [InlineData("file:///srv/repo", false)]
    public void A_credential_is_sent_in_clear_only_over_http_and_git(string url, bool expected)
    {
        Assert.Equal(expected, RepositoryUrl.SendsCredentialInClear(url, hasCredential: true));
        Assert.False(RepositoryUrl.SendsCredentialInClear(url, hasCredential: false));
    }
}
