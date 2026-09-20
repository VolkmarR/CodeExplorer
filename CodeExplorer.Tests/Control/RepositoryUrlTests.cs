using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What the API accepts as a repository URL, and what it refuses because it carries a secret. In
///     <c>Control/</c> with the classifier, which moved there when the clone stopped being its second
///     reader (ADR-0007 made every clone full, so there is no shallow decision left to keep in step).
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
    [InlineData("https://user:token@github.com/org/repo.git", RepositoryUrlKind.Invalid)]
    [InlineData("ssh://git:token@host/repo", RepositoryUrlKind.Invalid)]
    [InlineData("user:token@host:org/repo.git", RepositoryUrlKind.Invalid)]
    [InlineData("ftp://host/repo", RepositoryUrlKind.Invalid)]
    [InlineData("", RepositoryUrlKind.Invalid)]
    public void Repository_urls_are_classified_and_secrets_in_them_refused(string url, RepositoryUrlKind expected) =>
        Assert.Equal(expected, RepositoryUrl.Classify(url));
}
