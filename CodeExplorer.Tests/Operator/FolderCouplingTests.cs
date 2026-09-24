using CodeExplorer.Index;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview page's Folders that change together card (#213): pairs of top-level folders in one
///     repository that the window's commits touched together, counted in distinct commits. Asserted
///     against the JSON the browser receives, like the rest of the live overview.
/// </summary>
public sealed class FolderCouplingTests : IDisposable
{
    private TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_commit_touching_many_files_in_a_folder_counts_once()
    {
        // The fixture's own commit touches src twice, lib and docs; root files have no folder.
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/A.cs"] = "a\n", ["src/B.cs"] = "b\n", ["lib/C.cs"] = "c\n", ["docs/D.md"] = "d\n",
                ["Root.cs"] = "r\n"
            }
        });
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/A.cs"] = "a2\n", ["src/B.cs"] = "b2\n", ["lib/C.cs"] = "c2\n" },
            "Second", "Ada", "ada@example.invalid", 1);
        await _host.RefreshAsync("alpha");

        var coupling = await FolderCouplingAsync("alpha");

        var one = Assert.Single(coupling.Repositories);
        Assert.Equal(2, one.Commits);
        Assert.Equal([new FolderCommits("lib", 2), new FolderCommits("src", 2), new FolderCommits("docs", 1)],
            one.Folders);
        Assert.Equal(
            [new FolderPair("lib", "src", 2), new FolderPair("docs", "lib", 1), new FolderPair("docs", "src", 1)],
            one.Pairs);
        Assert.Equal(0, coupling.CeilingExcluded);
    }

    [Fact]
    public async Task A_commit_over_the_ceiling_is_left_out_and_counted()
    {
        _host.Dispose();
        _host = new TestHost(SearchEngine.Substring, maxCommitPaths: 3);
        // Four paths in the fixture's commit, over a ceiling of three.
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/A.cs"] = "a\n", ["lib/C.cs"] = "c\n", ["docs/D.md"] = "d\n", ["x/E.cs"] = "e\n" }
        });
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/A.cs"] = "a2\n", ["lib/C.cs"] = "c2\n" },
            "Second", "Ada", "ada@example.invalid", 1);
        await _host.RefreshAsync("alpha");

        var coupling = await FolderCouplingAsync("alpha");

        Assert.Equal([new FolderPair("lib", "src", 1)], Assert.Single(coupling.Repositories).Pairs);
        Assert.Equal(3, coupling.MaxCommitPaths);
        Assert.Equal(1, coupling.CeilingExcluded);
    }

    [Fact]
    public async Task Pairs_never_cross_a_repository_and_follow_the_filter()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["a/X.cs"] = "x\n", ["b/Y.cs"] = "y\n" },
            ["two"] = new() { ["a/Z.cs"] = "z\n", ["c/W.cs"] = "w\n" }
        });

        var all = await FolderCouplingAsync("alpha");
        var two = await FolderCouplingAsync("alpha", "?repository=two");

        Assert.Equal(["one", "two"], all.Repositories.Select(r => r.RepositorySlug));
        Assert.Equal([new FolderPair("a", "b", 1)], all.Repositories[0].Pairs);
        Assert.Equal([new FolderPair("a", "c", 1)], all.Repositories[1].Pairs);
        Assert.Equal(["two"], two.Repositories.Select(r => r.RepositorySlug));
    }

    [Fact]
    public async Task Excluded_paths_are_removed_before_pairing()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/A.cs"] = "a\n", ["lib/C.cs"] = "c\n", ["docs/D.md"] = "d\n" }
        });
        await _host.SetExcludedPathsAsync("alpha", ["one/docs/**"]);

        var coupling = await FolderCouplingAsync("alpha");

        var one = Assert.Single(coupling.Repositories);
        Assert.Equal(["lib", "src"], one.Folders.Select(f => f.Folder));
        Assert.Equal([new FolderPair("lib", "src", 1)], one.Pairs);
    }

    [Fact]
    public async Task A_project_with_no_history_has_no_pairs()
    {
        await _host.HistorylessProjectAsync("beta");

        Assert.Empty((await FolderCouplingAsync("beta")).Repositories);
    }

    private async Task<OverviewFolderCoupling> FolderCouplingAsync(string slug, string query = "") =>
        Assert.IsType<OverviewFolderCoupling>((await _host.OverviewDetailAsync(slug, query)).Cards?.FolderCoupling);
}
