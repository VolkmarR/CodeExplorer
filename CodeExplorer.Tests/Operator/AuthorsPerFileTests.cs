using CodeExplorer.Index;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview page's Most authors per file card (#212): the files at HEAD changed by the most
///     different people over the whole imported history, each with the commit share of its first three
///     authors. Asserted against the JSON the browser receives, like the rest of the live overview.
/// </summary>
public sealed class AuthorsPerFileTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Authors_are_counted_by_email_and_each_file_carries_its_first_three_shares()
    {
        // The fixture's own commit, by Test, adds all three files.
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["A.cs"] = "a\n", ["B.cs"] = "b\n", ["Gone.cs"] = "g\n" }
        });
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["A.cs"] = "a2\n", ["Gone.cs"] = "g2\n" },
            "Second", "Ada", "ada@example.invalid", 1);
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["A.cs"] = "a3\n", ["Gone.cs"] = "g3\n" },
            "Third", "Ada Lovelace", "ada@example.invalid", 2);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["A.cs"] = "a4\n", ["B.cs"] = "b2\n", ["Gone.cs"] = "g4\n" },
            "Fourth", "Bob", "bob@example.invalid", 3);
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["Gone.cs"] = "g5\n" },
            "Fifth", "Carol", "carol@example.invalid", 4);
        _host.RemoveInGitRepositoryAs("one", ["Gone.cs"], "Drop Gone", "Carol", "carol@example.invalid", 5);
        await _host.RefreshAsync("alpha");

        var authors = await AuthorsPerFileAsync("alpha");

        // Ada under two names is one author of A.cs with half its commits. Gone.cs had four authors and
        // is not at HEAD, so it does not rank.
        Assert.Equal(
            [
                new AuthoredFile("one/A.cs", 3, 4, 0.5, 0.25, 0.25),
                new AuthoredFile("one/B.cs", 2, 2, 0.5, 0.5, 0)
            ],
            authors.Files);
        Assert.Null(authors.Excluded);
    }

    [Fact]
    public async Task A_tie_in_authors_goes_to_the_most_commits_and_then_to_the_path()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["C.cs"] = "c\n", ["B.cs"] = "b\n", ["A.cs"] = "a\n" }
        });
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["C.cs"] = "c2\n" },
            "Second", "Test", "test@example.invalid", 1);

        await _host.RefreshAsync("alpha");

        Assert.Equal(["one/C.cs", "one/A.cs", "one/B.cs"],
            (await AuthorsPerFileAsync("alpha")).Files.Select(f => f.QualifiedPath));
    }

    [Fact]
    public async Task Excluded_paths_leave_the_ranking_and_are_counted()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["A.cs"] = "a\n", ["Strings.verified.txt"] = "s\n" }
        });
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["Strings.verified.txt"] = "s2\n" },
            "Second", "Ada", "ada@example.invalid", 1);
        await _host.RefreshAsync("alpha");
        await _host.SetExcludedPathsAsync("alpha", ["**/*.verified.txt"]);

        var hidden = await AuthorsPerFileAsync("alpha");
        var shown = await AuthorsPerFileAsync("alpha", "?showExcluded=true");

        Assert.Equal(["one/A.cs"], hidden.Files.Select(f => f.QualifiedPath));
        Assert.Equal(1, hidden.Excluded);
        Assert.Equal(["one/Strings.verified.txt", "one/A.cs"], shown.Files.Select(f => f.QualifiedPath));
        Assert.Null(shown.Excluded);
    }

    [Fact]
    public async Task The_ranking_follows_the_repository_and_not_the_window()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["A.cs"] = "a\n" }, ["two"] = new() { ["B.cs"] = "b\n" }
        });
        // Forty days after the fixture's commits, so a thirty-day window holds this one alone.
        _host.CommitToGitRepositoryAs("two", new Dictionary<string, string> { ["C.cs"] = "c\n" },
            "Later", "Ada", "ada@example.invalid", 40 * 24 * 60);
        await _host.RefreshAsync("alpha");

        Assert.Equal(["one/A.cs", "two/B.cs", "two/C.cs"],
            (await AuthorsPerFileAsync("alpha", "?days=30")).Files.Select(f => f.QualifiedPath));
        Assert.Equal(["two/B.cs", "two/C.cs"],
            (await AuthorsPerFileAsync("alpha", "?repository=two")).Files.Select(f => f.QualifiedPath));
    }

    [Fact]
    public async Task A_project_with_no_history_has_no_files_to_rank()
    {
        await _host.HistorylessProjectAsync("beta");
        await _host.SetExcludedPathsAsync("beta", ["**/*.rc"]);

        var authors = await AuthorsPerFileAsync("beta");

        Assert.Empty(authors.Files);
        Assert.Equal(0, authors.Excluded);
    }

    private async Task<OverviewAuthorsPerFile> AuthorsPerFileAsync(string slug, string query = "") =>
        Assert.IsType<OverviewAuthorsPerFile>((await _host.OverviewDetailAsync(slug, query)).AuthorsPerFile);
}
