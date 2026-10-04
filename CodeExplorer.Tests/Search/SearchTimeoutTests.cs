using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using CodeExplorer.Search;
using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     grep and <c>list_matches</c> run a caller's RE2 pattern, and some patterns cost far more than any
///     answer is worth: a dot repeated thousands of times makes RE2's DFA give up, and its NFA then pays
///     for every state on every byte (#364). <c>Search:TimeoutSeconds</c> stops such a search and says
///     why (#373). The hosts run with a limit of one second, so a pattern that would scan for half a
///     minute is refused in about that.
/// </summary>
public sealed class SearchTimeoutTests(SearchTimeoutFixture fixture) : IClassFixture<SearchTimeoutFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Measured on the fixture without a limit, on a 20-core laptop: about 39 s for the multiline grep,
    ///     13 s for the listing and 10 s for the line grep, against a refusal in 1.1 to 2.8 s, the slower
    ///     ones while another class ran beside it. Six seconds leaves a slower machine room for the
    ///     statement to reach DuckDB's next interrupt check, and still fails a limit that was ignored.
    /// </summary>
    private static readonly TimeSpan WellBefore = TimeSpan.FromSeconds(6);

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_search_past_its_time_limit_is_refused_and_the_next_search_runs(SearchEngine engine)
    {
        var host = await fixture.HostAsync(engine);
        await using var client = await host.ConnectAsync(SearchTimeoutFixture.Slug);

        var watch = Stopwatch.StartNew();
        string grep = await TestHost.CallAsync(client, "grep", new Dictionary<string, object?>
        {
            ["query"] = SearchTimeoutFixture.MultilinePattern, ["multiline"] = true
        });
        Assert.Equal(Refusal(1), grep);
        Assert.True(watch.Elapsed < WellBefore, $"grep was refused after {watch.Elapsed}.");

        watch.Restart();
        string listed = await TestHost.CallAsync(client, "list_matches", new Dictionary<string, object?>
        {
            ["query"] = SearchTimeoutFixture.LinePattern
        });
        Assert.Equal(Refusal(1), listed);
        Assert.True(watch.Elapsed < WellBefore, $"list_matches was refused after {watch.Elapsed}.");

        // The HTTP search runs the same grep and has no multiline mode, so it is refused for the line
        // pattern, with the status and the body of every other pattern it refuses.
        using var http = host.CreateClient();
        watch.Restart();
        using var response = await http.GetAsync(
            $"/api/projects/{SearchTimeoutFixture.Slug}/search?regex=true&q="
            + Uri.EscapeDataString(SearchTimeoutFixture.LinePattern), Ct);
        Assert.True(watch.Elapsed < WellBefore, $"/search was refused after {watch.Elapsed}.");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Refusal(1), body.RootElement.GetProperty("error").GetString());

        // An interrupted connection is not handed to the next search broken: each shape runs again.
        Assert.Contains("200 files match in total", await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Needle", ["multiline"] = true }), StringComparison.Ordinal);
        Assert.Contains("200 files match in total", await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Needle" }), StringComparison.Ordinal);
        Assert.Contains("Needle", await TestHost.CallAsync(client, "list_matches",
            new Dictionary<string, object?> { ["query"] = "Need(le)" }), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A caller that gives up before the limit is not answered: its cancellation propagates as it did
    ///     before there was a limit, and the limit, which fires while the statement is still stopping, does
    ///     not turn it into a refusal.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_caller_that_gives_up_is_cancelled_and_not_refused(SearchEngine engine)
    {
        var host = await fixture.HostAsync(engine);
        var grep = host.Services.GetRequiredService<GrepSearch>();
        var matches = host.Services.GetRequiredService<MatchList>();

        await AssertCancelledAsync(token => grep.SearchAsync(SearchTimeoutFixture.Slug,
            new GrepRequest(SearchTimeoutFixture.MultilinePattern, Multiline: true), token));
        await AssertCancelledAsync(token => matches.ListAsync(SearchTimeoutFixture.Slug,
            new MatchListRequest(SearchTimeoutFixture.LinePattern, new FileFilter()), token));
    }

    [Fact]
    public void The_limit_is_twenty_seconds_where_nothing_configures_it() =>
        Assert.Equal(20, SearchTimeout.Seconds(Settings.Of([])));

    /// <summary>
    ///     DuckDB reports a statement it interrupted with its own exception, and a token already cancelled
    ///     before the next statement throws the cancellation; either is the caller's cancellation arriving,
    ///     and neither is an answer.
    /// </summary>
    private static async Task AssertCancelledAsync(Func<CancellationToken, Task<Outcome>> search)
    {
        using var gaveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        gaveUp.CancelAfter(TimeSpan.FromMilliseconds(300));
        var thrown = await Record.ExceptionAsync(() => search(gaveUp.Token));
        Assert.True(thrown is OperationCanceledException || (thrown is DuckDBException duck && PatternQuery.IsInterrupt(duck)),
            $"Expected the cancellation, got {thrown?.GetType().Name ?? "an answer"}.");
    }

    private static string Refusal(int seconds) =>
        $"The search was stopped after {seconds} s: this pattern is too expensive to run over the index. "
        + "Narrow it with `path` or `extension`, or use a simpler pattern, for example without `.*` spanning lines.";
}

/// <summary>
///     One server per engine, each with a one-second limit and a project of two hundred small files,
///     built once for the class: the build is most of what these tests cost.
///     Each file starts with a line the patterns match cheaply. That keeps every 2048-line vector of a
///     scan non-empty: a scan whose filter rejects a whole vector reads on to the next one without
///     returning, so a pattern that matched nothing would only be interrupted between row groups.
/// </summary>
public sealed class SearchTimeoutFixture : IAsyncLifetime
{
    public const string Slug = "slow";

    /// <summary>
    ///     A dot spanning a thousand characters, sixty-four times: it fits RE2's size limit only where the
    ///     dot crosses newlines, and RE2's DFA gives up on it (#364). The alternative is what matches.
    /// </summary>
    public static readonly string MultilinePattern = "Needle|" + string.Concat(Enumerable.Repeat("(?:.{1000})", 64));

    /// <summary>
    ///     The same for a search by line, half as wide: without the <c>s</c> flag the full width does not
    ///     fit RE2's size limit, and a chunk of lines scanned under it takes long enough to delay the
    ///     interrupt by seconds.
    /// </summary>
    public static readonly string LinePattern = "Needle|" + string.Concat(Enumerable.Repeat("(?:.{1000})", 32));

    private readonly Dictionary<SearchEngine, Task<TestHost>> _hosts = [];
    private readonly Lock _lock = new();

    public Task<TestHost> HostAsync(SearchEngine engine)
    {
        lock (_lock)
        {
            if (!_hosts.TryGetValue(engine, out var host)) _hosts[engine] = host = BuildAsync(engine);
            return host;
        }
    }

    private static async Task<TestHost> BuildAsync(SearchEngine engine)
    {
        var host = new TestHost(engine, (SearchTimeout.Setting, 1));
        string filler = string.Concat(Enumerable.Repeat(new string('x', 49) + "\n", 99));
        await host.IndexedProjectAsync(Slug, new Dictionary<string, Dictionary<string, string>>
        {
            [Slug] = Enumerable.Range(0, 200).ToDictionary(i => $"src/File{i}.txt", _ => "Needle\n" + filler)
        });
        return host;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values) (await host).Dispose();
    }
}
