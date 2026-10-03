using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using ModelContextProtocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The tool-call counts the web UI shows. The rings are tested on <see cref="ToolCallLog" /> with the
///     clock passed in, and the wiring through a real MCP call: a count nothing reaches is worth nothing.
///     Each host test owns a slug of its own, because the listener is process-wide and every host in
///     the suite counts every other host's calls too.
/// </summary>
public sealed class ToolStatisticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Calls_are_totalled_per_tool_and_counted_into_their_minute()
    {
        var log = new ToolCallLog();
        log.Record(Noon.AddMinutes(-2), "grep", 0.2, failed: false);
        log.Record(Noon.AddMinutes(-2).AddSeconds(30), "grep", 0.4, failed: true);
        log.Record(Noon, "read_file", 0.01, failed: false);

        var snapshot = log.Snapshot(Noon.AddSeconds(10), Noon.AddHours(-1));

        Assert.Equal(3, snapshot.CallsLastHour);
        Assert.Equal(1, snapshot.FailedLastHour);
        Assert.Equal(ToolCallLog.MinutesKept, snapshot.PerMinute.Count);
        Assert.Equal(Noon, snapshot.PerMinute[^1].Minute);
        Assert.Equal((1, 0), (snapshot.PerMinute[^1].Calls, snapshot.PerMinute[^1].Failed));
        Assert.Equal((2, 1), (snapshot.PerMinute[^3].Calls, snapshot.PerMinute[^3].Failed));

        var grep = snapshot.Tools[0];
        Assert.Equal(("grep", 2L, 1L, 0.4), (grep.Tool, grep.Calls, grep.Failed, grep.MaxSeconds));
        Assert.Equal(Noon.AddMinutes(-2).AddSeconds(30), grep.LastCall);
        Assert.Equal(["read_file", "grep", "grep"], snapshot.Recent.Select(c => c.Tool));
    }

    /// <summary>
    ///     A minute the ring comes round to again holds a call from an hour ago. It has to read as empty
    ///     and not as this minute's, or a project idle for an hour would show its last burst as current.
    /// </summary>
    [Fact]
    public void Minutes_older_than_the_window_drop_out_of_it()
    {
        var log = new ToolCallLog();
        log.Record(Noon, "grep", 0.1, failed: false);

        var hourLater = log.Snapshot(Noon.AddMinutes(ToolCallLog.MinutesKept), Noon);
        Assert.Equal(0, hourLater.CallsLastHour);
        Assert.All(hourLater.PerMinute, m => Assert.Equal(0, m.Calls));
        // The totals are since the replica started, not over the window.
        Assert.Equal(1, Assert.Single(hourLater.Tools).Calls);

        log.Record(Noon.AddMinutes(ToolCallLog.MinutesKept), "grep", 0.1, failed: false);
        var next = log.Snapshot(Noon.AddMinutes(ToolCallLog.MinutesKept), Noon);
        Assert.Equal(1, next.CallsLastHour);
    }

    [Fact]
    public void The_recent_calls_and_percentiles_keep_only_the_latest()
    {
        var log = new ToolCallLog();
        for (int i = 1; i <= ToolCallLog.RecentKept + 10; i++)
            log.Record(Noon.AddSeconds(i), "grep", i, failed: false);

        var snapshot = log.Snapshot(Noon.AddMinutes(10), Noon);

        Assert.Equal(ToolCallLog.RecentShown, snapshot.Recent.Count);
        Assert.Equal(ToolCallLog.RecentKept + 10, snapshot.Recent[0].Seconds);
        var grep = Assert.Single(snapshot.Tools);
        Assert.Equal(ToolCallLog.RecentKept + 10, grep.Calls);
        // The last hundred calls took 111 to 210 seconds, so their median is the fiftieth of those.
        Assert.Equal(160, grep.P50Seconds);
        Assert.Equal(205, grep.P95Seconds);
        Assert.Equal(ToolCallLog.RecentKept + 10, grep.MaxSeconds);
    }

    [Fact]
    public void A_project_no_agent_has_called_answers_with_zeros()
    {
        var empty = ToolCallLog.Empty(Noon, Noon);

        Assert.Equal(0, empty.CallsLastHour);
        Assert.Null(empty.P95Seconds);
        Assert.Empty(empty.Tools);
        Assert.Empty(empty.Recent);
        Assert.Equal(ToolCallLog.MinutesKept, empty.PerMinute.Count);
    }

    [Fact]
    public async Task A_tool_call_reaches_the_project_page_and_the_project_list()
    {
        const string slug = "stats-call";
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync(slug, Repository());

        await using (var client = await host.ConnectAsync(slug))
        {
            await TestHost.CallAsync(client, "grep", new Dictionary<string, object?> { ["query"] = "Alpha" });
            await TestHost.CallAsync(client, "grep", new Dictionary<string, object?> { ["query"] = "Beta" });
        }

        var statistics = await host.GetJsonAsync<ToolCallStatistics>($"/api/projects/{slug}/tool-calls");
        Assert.Equal(2, statistics.CallsLastHour);
        Assert.Equal(0, statistics.FailedLastHour);
        var grep = Assert.Single(statistics.Tools);
        Assert.Equal(("grep", 2L, 0L), (grep.Tool, grep.Calls, grep.Failed));

        var activity = await host.GetJsonAsync<ProjectToolActivity[]>("/api/tool-calls");
        Assert.Equal(2, Assert.Single(activity, a => a.Project == slug).CallsLastHour);
    }

    /// <summary>
    ///     A tool that throws is counted as failed. The SDK turns the throw into an <c>IsError</c> result
    ///     only outside the request filters, so <see cref="Telemetry.ToolFilter" /> sees the exception
    ///     itself. The failure is a restore whose Parquet file is held open, as in <c>DurabilityTests</c>.
    /// </summary>
    [Fact]
    public async Task A_tool_that_throws_is_counted_as_failed()
    {
        const string slug = "stats-failed";
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync(slug, Repository());
        host.DeleteIndexFile(slug);
        host.Restart();

        using var probe = new TelemetryProbe(slug);
        await using (File.Open(Path.Combine(host.DurableIndexDirectory(slug), "lines.parquet"),
                         FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await using var client = await host.ConnectAsync(slug);
            await Assert.ThrowsAsync<McpException>(() => TestHost.CallAsync(client, "project_overview", []));
        }

        Assert.Equal(Telemetry.FailedOutcome,
            Assert.Single(probe.For(Telemetry.ToolDuration)).Tags[Telemetry.OutcomeTag]);

        var statistics = await host.GetJsonAsync<ToolCallStatistics>($"/api/projects/{slug}/tool-calls");
        Assert.Equal(1,statistics.FailedLastHour);
        Assert.True(Assert.Single(statistics.Recent).Failed);
    }

    private static Dictionary<string, Dictionary<string, string>> Repository() =>
        new() { ["one"] = new Dictionary<string, string> { ["src/A.cs"] = "class Alpha;\nclass Beta;\n" } };
}
