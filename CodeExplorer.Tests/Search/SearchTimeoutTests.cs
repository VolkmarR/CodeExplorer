using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
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
    ///     How much of the lines the reference scan of <see cref="WholeScanAsync" /> reads: a twentieth,
    ///     6000 lines or nearly three vectors, about a second on a 20-core laptop with nothing else running. Enough vectors
    ///     that one vector's noise does not decide the estimate, and few enough that the test still pays
    ///     little for it under load.
    /// </summary>
    private const int ReferenceShare = 20;

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_search_past_its_time_limit_is_refused_and_the_next_search_runs(SearchEngine engine)
    {
        var host = await fixture.HostAsync(engine);
        await using var client = await host.ConnectAsync(SearchTimeoutFixture.Slug);

        // No time bound for the multiline grep: it stops after about half of what it would cost to finish,
        // whatever the number of files, so no bound tells stopped from finished under every load. Refused
        // over the 200 files it is scoped to, it took 3.0 to 3.5 s alone, against 6.7 to 7.1 s to finish.
        string grep = await TestHost.CallAsync(client, "grep", new Dictionary<string, object?>
        {
            ["query"] = SearchTimeoutFixture.MultilinePattern, ["multiline"] = true,
            ["ext"] = SearchTimeoutFixture.MultilineExtension
        });
        Assert.Equal(Refusal(1), grep);

        // A line search is refused within one vector of the limit, and scans all of its nearly sixty only
        // when the limit is ignored, so it is held to half the whole scan, measured under the same load.
        var wholeScan = await WholeScanAsync(host);

        var watch = Stopwatch.StartNew();
        string listed = await TestHost.CallAsync(client, "list_matches", new Dictionary<string, object?>
        {
            ["query"] = SearchTimeoutFixture.LinePattern
        });
        Assert.Equal(Refusal(1), listed);
        AssertStopped("list_matches", watch.Elapsed, wholeScan);

        // The HTTP search runs the same grep and has no multiline mode, so it is refused for the line
        // pattern, with the status and the body of every other pattern it refuses.
        using var http = host.CreateClient();
        watch.Restart();
        using var response = await http.GetAsync(
            $"/api/projects/{SearchTimeoutFixture.Slug}/search?regex=true&q="
            + Uri.EscapeDataString(SearchTimeoutFixture.LinePattern), Ct);
        AssertStopped("/search", watch.Elapsed, wholeScan);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Refusal(1), body.RootElement.GetProperty("error").GetString());

        // An interrupted connection is not handed to the next search broken: each shape runs again. The
        // multiline one keeps to its 200 files: over all of them, it passed the limit under load.
        Assert.Contains($"{SearchTimeoutFixture.MultilineFiles} files match in total", await TestHost.CallAsync(client,
            "grep", new Dictionary<string, object?>
            {
                ["query"] = "Needle", ["multiline"] = true, ["ext"] = SearchTimeoutFixture.MultilineExtension
            }), StringComparison.Ordinal);
        Assert.Contains($"{SearchTimeoutFixture.Files} files match in total", await TestHost.CallAsync(client, "grep",
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
            new GrepRequest(SearchTimeoutFixture.MultilinePattern, Extension: SearchTimeoutFixture.MultilineExtension,
                Multiline: true), token));
        await AssertCancelledAsync(token => matches.ListAsync(SearchTimeoutFixture.Slug,
            new MatchListRequest(SearchTimeoutFixture.LinePattern, new FileFilter()), token));
    }

    /// <summary>
    ///     A search runs without the limit while its plans are recorded, because each statement then runs
    ///     twice. Only its own server's recording counts: every test class runs a server in this process,
    ///     and a recording held by another one switched the limit off here, so a pattern the limit should
    ///     have stopped ran to the end and was answered.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_plan_recorded_for_another_server_leaves_the_limit_on(SearchEngine engine)
    {
        var host = await fixture.HostAsync(engine);
        await using var client = await host.ConnectAsync(SearchTimeoutFixture.Slug);
        // Neither directory is written to: no read of this server's index is recorded.
        string elsewhere = Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

        string listed;
        using (QueryPlan.Recording(Path.Combine(elsewhere, "plans"), Path.Combine(elsewhere, "data")))
            listed = await TestHost.CallAsync(client, "list_matches", new Dictionary<string, object?>
            {
                ["query"] = SearchTimeoutFixture.LinePattern
            });

        Assert.Equal(Refusal(1), listed);
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

    /// <summary>
    ///     About how long the line pattern takes over every line with no limit, measured now, so it carries
    ///     the load the searches beside it carry: the pattern over a <see cref="ReferenceShare" />th of the
    ///     lines, run straight on the index where no limit applies, and scaled up. The scale holds because
    ///     the lines fit one row group, which DuckDB scans on one thread, vector after vector; it is
    ///     asserted, because lines spread over several row groups are scanned side by side, and the whole
    ///     scan would then be faster than the estimate says, which would let a search that was not stopped
    ///     pass. Only the statement is timed: opening the index is not the pattern's cost, and under load
    ///     it would be scaled up with it.
    /// </summary>
    private static async Task<TimeSpan> WholeScanAsync(TestHost host)
    {
        using var lease = await host.OpenIndexAsync(SearchTimeoutFixture.Slug);
        Assert.Equal(["1"], await TestHost.ScalarsAsync(lease,
            "SELECT count(DISTINCT row_group_id)::VARCHAR FROM pragma_storage_info('lines')"));

        var watch = Stopwatch.StartNew();
        await TestHost.ScalarsAsync(lease, $"""
                                            SELECT count(*)::VARCHAR
                                            FROM (SELECT content FROM lines LIMIT {SearchTimeoutFixture.Lines / ReferenceShare})
                                            WHERE regexp_matches(content, '{SearchTimeoutFixture.LinePattern}', 'i')
                                            """);
        var elapsed = watch.Elapsed;
        lease.Completed();
        return elapsed * ReferenceShare;
    }

    /// <summary>
    ///     A search stopped by the limit ends within one vector of it; one the limit only refused once it
    ///     finished took the whole scan. Half the whole scan is far from both, alone and under load.
    /// </summary>
    private static void AssertStopped(string search, TimeSpan refusedAfter, TimeSpan wholeScan) =>
        Assert.True(refusedAfter < wholeScan / 2,
            $"{search} was refused after {refusedAfter}, and the whole scan takes about {wholeScan}.");

    private static string Refusal(int seconds) =>
        $"The search was stopped after {seconds} s: this pattern is too expensive to run over the index. "
        + "Narrow it with `path` or `extension`, or use a simpler pattern, for example without `.*` spanning lines.";
}

/// <summary>
///     One server per engine, each with a one-second limit and a project of small files, built once for
///     the class: the build is most of what these tests cost.
///     Each file starts with a line the patterns match cheaply. That keeps every 2048-line vector of a
///     scan non-empty: a scan whose filter rejects a whole vector reads on to the next one without
///     returning, so a pattern that matched nothing would only be interrupted between row groups.
/// </summary>
public sealed class SearchTimeoutFixture : IAsyncLifetime
{
    public const string Slug = "slow";

    /// <summary>
    ///     As many lines as fit one row group of 122,880, so the whole scan of a line search is nearly sixty
    ///     vectors on one thread. That keeps it far from a stop within one vector of the limit, about
    ///     24 s against 1.4 s alone, and lets a share of it be scaled up. More lines would not make
    ///     the scan slower: DuckDB scans the next row group on another thread beside the first.
    /// </summary>
    public const int Files = 1200;

    public const int LinesPerFile = 100;

    public const int Lines = Files * LinesPerFile;

    /// <summary>
    ///     The files the multiline grep is scoped to. Its stop grows with its files: it runs the pattern
    ///     over a whole chunk of documents at a time, and refused over two thousand files it went on for
    ///     25 s past the limit, against 2.3 s over two hundred.
    /// </summary>
    public const int MultilineFiles = 200;

    /// <summary>The extension of the <see cref="MultilineFiles" />, and only of them.</summary>
    public const string MultilineExtension = "txt";

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
        string content = "Needle\n" + string.Concat(Enumerable.Repeat(new string('x', 49) + "\n", LinesPerFile - 1));
        await host.IndexedProjectAsync(Slug, new Dictionary<string, Dictionary<string, string>>
        {
            [Slug] = Enumerable.Range(0, Files).ToDictionary(
                i => $"src/File{i}.{(i < MultilineFiles ? MultilineExtension : "log")}", _ => content)
        });
        return host;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values) (await host).Dispose();
    }
}
