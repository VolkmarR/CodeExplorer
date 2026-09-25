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
}
