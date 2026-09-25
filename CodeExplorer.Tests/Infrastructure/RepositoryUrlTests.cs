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
