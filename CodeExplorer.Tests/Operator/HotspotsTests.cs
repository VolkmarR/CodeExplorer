using CodeExplorer.Index;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview page's Hotspots card (#211): the files at HEAD ranked by their commits in the
///     page's window times their lines at HEAD, over the page's filters and without the project's
///     excluded paths. Asserted against the JSON the browser receives, like the rest of the live overview.
/// </summary>
public sealed class HotspotsTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Files_rank_by_commits_times_lines_and_a_tie_goes_to_the_most_lines_changed()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["Both.cs"] = Lines(20), ["Big.cs"] = Lines(40), ["Z_tie.cs"] = Lines(10),
                ["A_tie.cs"] = Lines(5), ["Gone.cs"] = Lines(30), ["logo.bin"] = "\0\0" + Lines(50)
            }
        });
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string>
            {
                ["Both.cs"] = Lines(20, "b"), ["A_tie.cs"] = Lines(4) + "changed\n", ["Gone.cs"] = Lines(30, "g"),
                ["logo.bin"] = "\0\0" + Lines(50, "l")
            }, "Second", "Ada", "ada@example.invalid", 1);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string>
            {
                ["Both.cs"] = Lines(20, "c"), ["Gone.cs"] = Lines(30, "h"), ["logo.bin"] = "\0\0" + Lines(50, "m")
            }, "Third", "Ada", "ada@example.invalid", 2);
        _host.RemoveInGitRepositoryAs("one", ["Gone.cs"], "Drop Gone", "Ada", "ada@example.invalid", 3);
        await _host.RefreshAsync("alpha");

        var hotspots = await HotspotsAsync("alpha");

        // Z_tie and A_tie both score 10. Z_tie changed ten lines in its one commit and A_tie seven over
        // its two, so Z_tie ranks first although its path sorts last: the tie breaks the way churn's does.
        // Gone.cs is busier than any of them and gone at HEAD; logo.bin is as busy and has no lines.
        Assert.Equal(
            [
                new Hotspot("one/Both.cs", 3, 20, 60), new Hotspot("one/Big.cs", 1, 40, 40),
                new Hotspot("one/Z_tie.cs", 1, 10, 10), new Hotspot("one/A_tie.cs", 2, 5, 10)
            ],
            hotspots.Files);
        Assert.Null(hotspots.Excluded);
    }

    [Fact]
    public async Task A_tie_in_score_and_lines_changed_goes_to_the_path()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["B.cs"] = Lines(10), ["A.cs"] = Lines(10) }
        });

        Assert.Equal(["one/A.cs", "one/B.cs"], (await HotspotsAsync("alpha")).Files.Select(f => f.QualifiedPath));
    }

    [Fact]
    public async Task A_repository_the_project_does_not_have_has_no_hotspots_to_show()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["A.cs"] = Lines(10) }
        });

        var detail = await _host.OverviewDetailAsync("alpha", "?repository=nope");

        // Set with the overview and never without it, which is what the page's card is drawn on.
        Assert.NotNull(detail.Unavailable);
        Assert.Null(detail.Hotspots);
    }

    [Fact]
    public async Task Excluded_paths_leave_the_ranking_and_are_counted()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["A.cs"] = Lines(10), ["Strings.verified.txt"] = Lines(500) }
        });
        await _host.SetExcludedPathsAsync("alpha", ["**/*.verified.txt"]);

        var hidden = await HotspotsAsync("alpha");
        var shown = await HotspotsAsync("alpha", "?showExcluded=true");

        Assert.Equal(["one/A.cs"], hidden.Files.Select(f => f.QualifiedPath));
        Assert.Equal(1, hidden.Excluded);
        Assert.Equal(["one/Strings.verified.txt", "one/A.cs"], shown.Files.Select(f => f.QualifiedPath));
        Assert.Null(shown.Excluded);
    }

    [Fact]
    public async Task The_ranking_follows_the_window_and_the_repository()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["A.cs"] = Lines(10) }, ["two"] = new() { ["B.cs"] = Lines(10) }
        });
        // Forty days after the fixture's commits, so a thirty-day window holds this one alone.
        _host.CommitToGitRepositoryAs("two", new Dictionary<string, string> { ["C.cs"] = Lines(3) },
            "Later", "Ada", "ada@example.invalid", 40 * 24 * 60);
        await _host.RefreshAsync("alpha");

        Assert.Equal(["two/C.cs"], (await HotspotsAsync("alpha", "?days=30")).Files.Select(f => f.QualifiedPath));
        Assert.Equal(["two/B.cs", "two/C.cs"],
            (await HotspotsAsync("alpha", "?repository=two")).Files.Select(f => f.QualifiedPath));
    }

    [Fact]
    public async Task A_project_with_no_history_has_no_hotspots()
    {
        await _host.HistorylessProjectAsync("beta");
        await _host.SetExcludedPathsAsync("beta", ["**/*.rc"]);

        var hotspots = await HotspotsAsync("beta");

        Assert.Empty(hotspots.Files);
        Assert.Equal(0, hotspots.Excluded);
    }

    /// <summary><paramref name="count" /> distinct lines, the prefix making one version differ from the next.</summary>
    private static string Lines(int count, string prefix = "") =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix}line {i}\n"));

    private async Task<OverviewHotspots> HotspotsAsync(string slug, string query = "") =>
        Assert.IsType<OverviewHotspots>((await _host.OverviewDetailAsync(slug, query)).Hotspots);
}
