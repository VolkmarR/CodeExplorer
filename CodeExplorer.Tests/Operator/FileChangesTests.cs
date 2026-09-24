using CodeExplorer.Index;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview page's Files added and deleted card (#214): 24 calendar months ending at the month of
///     the newest recorded commit, each with the files added, deleted and renamed. Asserted against the
///     JSON the browser receives, like the rest of the live overview. The fixtures' commits are dated in
///     minutes past the epoch, so the months below are 1970's.
/// </summary>
public sealed class FileChangesTests : IDisposable
{
    /// <summary>Minutes from the epoch to the start of a 1970 month.</summary>
    private const int _february = 31 * 1440, _march = (31 + 28) * 1440, _april = (31 + 28 + 31) * 1440;

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Months_count_adds_deletes_and_renames_apart_and_end_at_the_newest_commit()
    {
        // The fixture's own commit, at the epoch, adds three files.
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["A.cs"] = "alpha content\n", ["B.cs"] = "b\n", ["docs/D.md"] = "d\n" }
        });
        // A minute before February: still January however the session's time zone reads it.
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["E.cs"] = "e\n" },
            "Late", "Ada", "ada@example.invalid", _february - 1);
        _host.RemoveInGitRepositoryAs("one", ["B.cs"], "Drop", "Ada", "ada@example.invalid", _march + 10);
        _host.MoveInGitRepositoryAs("one", new Dictionary<string, string> { ["A.cs"] = "src/A.cs" }, "Move",
            "Ada", "ada@example.invalid", _april + 10);
        await _host.RefreshAsync("alpha");

        var months = (await FileChangesAsync("alpha")).Months;

        Assert.Equal(24, months.Count);
        Assert.Equal((1968, 5), (months[0].Year, months[0].Month));
        Assert.Equal(
            [new MonthChanges(1970, 1, 4, 0, 0), new MonthChanges(1970, 2, 0, 0, 0),
                new MonthChanges(1970, 3, 0, 1, 0), new MonthChanges(1970, 4, 0, 0, 1)],
            months.TakeLast(4));
        Assert.All(months.SkipLast(4), m => Assert.Equal((0, 0, 0), (m.Added, m.Deleted, m.Renamed)));
    }

    [Fact]
    public async Task Excluded_paths_are_not_counted()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/A.cs"] = "a\n", ["docs/D.md"] = "d\n" }
        });
        await _host.SetExcludedPathsAsync("alpha", ["one/docs/**"]);

        Assert.Equal(1, (await FileChangesAsync("alpha")).Months[^1].Added);
        Assert.Equal(2, (await FileChangesAsync("alpha", "?showExcluded=true")).Months[^1].Added);
    }

    [Fact]
    public async Task A_project_with_no_history_has_no_months()
    {
        await _host.HistorylessProjectAsync("beta");

        Assert.Empty((await FileChangesAsync("beta")).Months);
    }

    private async Task<OverviewFileChanges> FileChangesAsync(string slug, string query = "") =>
        Assert.IsType<OverviewFileChanges>((await _host.OverviewDetailAsync(slug, query)).Cards?.FileChanges);
}
