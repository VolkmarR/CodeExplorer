using System.Globalization;
using CodeExplorer.Index;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview page's Files added and deleted card (#214): the files added, deleted and renamed over
///     the page's window, a bar per day, week or month by the window's length. Asserted against the JSON
///     the browser receives, like the rest of the live overview. The fixtures' commits are dated in
///     minutes past the epoch, so the periods below are 1970's.
/// </summary>
public sealed class FileChangesTests : IDisposable
{
    /// <summary>Minutes from the epoch to the start of a 1970 month.</summary>
    private const int _february = 31 * 1440, _march = (31 + 28) * 1440, _april = (31 + 28 + 31) * 1440;

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    /// <summary>
    ///     Four changes, one per month from January to April 1970: the fixture's own commit at the epoch
    ///     adds three files, a minute before February adds one, March deletes one and April moves one. The
    ///     move is the newest commit, ten minutes into April, and every window ends there.
    /// </summary>
    private async Task ChangesInFourMonthsAsync()
    {
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
    }

    [Fact]
    public async Task A_year_is_drawn_by_month_and_counts_adds_deletes_and_renames_apart()
    {
        await ChangesInFourMonthsAsync();

        var changes = await FileChangesAsync("alpha", "?days=365");

        Assert.Equal(ChangePeriod.Month, changes.Period);
        // April 1969, clipped to the window, through April 1970.
        Assert.Equal(13, changes.Periods.Count);
        Assert.Equal(new DateOnly(1969, 4, 1), changes.Periods[0].Start);
        Assert.Equal(
            [new PeriodChanges(new DateOnly(1970, 1, 1), 4, 0, 0),
                new PeriodChanges(new DateOnly(1970, 2, 1), 0, 0, 0),
                new PeriodChanges(new DateOnly(1970, 3, 1), 0, 1, 0),
                new PeriodChanges(new DateOnly(1970, 4, 1), 0, 0, 1)],
            changes.Periods.TakeLast(4));
        Assert.All(changes.Periods.SkipLast(4), p => Assert.Equal((0, 0, 0), (p.Added, p.Deleted, p.Renamed)));
    }

    [Fact]
    public async Task A_quarter_is_drawn_by_week_from_monday_and_leaves_out_what_is_before_the_window()
    {
        await ChangesInFourMonthsAsync();

        // The default window: 90 days back from ten minutes into April is ten minutes into January, so
        // the fixture's own commit at the epoch falls outside it.
        var changes = await FileChangesAsync("alpha");

        Assert.Equal(ChangePeriod.Week, changes.Period);
        Assert.All(changes.Periods, p => Assert.Equal(DayOfWeek.Monday, p.Start.DayOfWeek));
        // 1 January 1970 was a Thursday, so the first week starts on the Monday before it.
        Assert.Equal(new DateOnly(1969, 12, 29), changes.Periods[0].Start);
        Assert.Equal((1, 1, 1),
            (changes.Periods.Sum(p => p.Added), changes.Periods.Sum(p => p.Deleted),
                changes.Periods.Sum(p => p.Renamed)));
    }

    [Fact]
    public async Task A_month_is_drawn_by_day()
    {
        await ChangesInFourMonthsAsync();

        var changes = await FileChangesAsync("alpha", "?days=30");

        Assert.Equal(ChangePeriod.Day, changes.Period);
        // 2 March, clipped to the window, through 1 April: the delete on 1 March is outside it.
        Assert.Equal(new DateOnly(1970, 3, 2), changes.Periods[0].Start);
        Assert.Equal(new PeriodChanges(new DateOnly(1970, 4, 1), 0, 0, 1), changes.Periods[^1]);
        Assert.Equal(31, changes.Periods.Count);
        Assert.Equal(0, changes.Periods.Sum(p => p.Deleted));
    }

    [Fact]
    public async Task A_window_before_the_epoch_is_drawn_under_a_culture_with_its_own_minus_sign()
    {
        await ChangesInFourMonthsAsync();
        // Swedish writes a negative number with U+2212, which SQL does not read as a minus. The year's
        // window starts in April 1969, so every period before January has negative epoch bounds. Read on
        // the test's own thread, because the in-process server does not carry the test's culture over.
        using var lease = await _host.OpenIndexAsync("alpha");
        var window = await IndexQueries.WindowAsync(lease.Connection, 365, null, TestContext.Current.CancellationToken);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var changes = await OverviewQueries.FileChangesAsync(lease.Connection, new ProjectPaths(true, "one"),
                new OverviewScope(365, null, ExcludedPaths.None), window, TestContext.Current.CancellationToken);

            Assert.Equal(13, changes.Periods.Count);
            Assert.Equal(new DateOnly(1969, 4, 1), changes.Periods[0].Start);
            Assert.Equal(4, changes.Periods.Sum(p => p.Added));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task Excluded_paths_are_not_counted()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/A.cs"] = "a\n", ["docs/D.md"] = "d\n" }
        });
        await _host.SetExcludedPathsAsync("alpha", ["one/docs/**"]);

        Assert.Equal(1, (await FileChangesAsync("alpha")).Periods[^1].Added);
        Assert.Equal(2, (await FileChangesAsync("alpha", "?showExcluded=true")).Periods[^1].Added);
    }

    [Fact]
    public async Task A_project_with_no_history_has_no_periods()
    {
        await _host.HistorylessProjectAsync("beta");

        Assert.Empty((await FileChangesAsync("beta")).Periods);
    }

    private async Task<OverviewFileChanges> FileChangesAsync(string slug, string query = "") =>
        Assert.IsType<OverviewFileChanges>((await _host.OverviewDetailAsync(slug, query)).Cards?.FileChanges);
}
