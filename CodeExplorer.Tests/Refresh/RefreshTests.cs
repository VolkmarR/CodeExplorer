using System.Net;
using System.Net.Http.Json;
using DuckDB.NET.Data;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A refresh fetches, rebuilds into a shadow index and swaps it in. Every test here is about a
///     state a client can catch the server in — mid-rebuild, mid-swap, refused — because the happy
///     path is already covered wherever a test builds a project at all.
/// </summary>
public sealed class RefreshTests : IDisposable
{
    private const string OldFile = "src/A.cs";
    private const string NewFile = "src/B.cs";

    // Substring throughout: nothing here asserts ranking, and pinning the engine keeps the suite
    // proving the same thing offline as on a machine that can install fts.
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refresh_picks_up_commits_pushed_after_the_clone()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        // The clone was made during the first refresh, so this commit exists only on the remote until
        // something fetches. Before #8 nothing did, and a rebuild re-read the tree from clone time.
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });

        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(2, summary.Files);
        Assert.Equal(["one/src/A.cs", "one/src/B.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    [Fact]
    public async Task A_query_in_flight_sees_the_whole_old_index_and_the_swap_waits_for_it()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });

        // An in-flight query: a lease is what a grep or a file read holds while it works, and the
        // drain is what makes a swap wait for one.
        using (var inFlight = await _host.OpenIndexAsync("alpha"))
        {
            using (var response = await _host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            await WaitForSwapAsync(_host, "alpha");

            // The shadow index is complete and the swap is blocked on this connection, which is the
            // one moment a half-applied refresh could be visible. It is not: the old index is intact.
            Assert.Equal(["one/src/A.cs"], await TestHost.ScalarsAsync(inFlight, PathQuery));
            // And it really is blocked, rather than having quietly finished before this ran.
            Assert.False(_host.Refreshes.Pending.IsCompleted);
        }

        await _host.WaitForRefreshesAsync();
        Assert.Equal(RefreshState.Succeeded, (await _host.RefreshStatusAsync("alpha")).State);
        Assert.Equal(["one/src/A.cs", "one/src/B.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    [Fact]
    public async Task The_search_endpoints_answer_from_the_old_index_throughout_a_rebuild()
    {
        // A one-second drain, so a test that deliberately never lets the swap through is over in a
        // second rather than in the default thirty. What it proves does not depend on the length: the
        // searches below run while the drain is waiting, which is the state this is about.
        using var host = new TestHost(SearchEngine.Substring, 1);
        await host.IndexedProjectAsync("alpha", Fixture());
        host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });

        using var http = host.Factory.CreateClient();
        using (await host.OpenIndexAsync("alpha"))
        {
            using (var response = await host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await WaitForSwapAsync(host, "alpha");

            // Not a lease this time but what a browser and an agent actually call, while the shadow
            // index is finished and the swap is waiting to put it in. Both answer, and answer as the
            // old index: a search during a rebuild is never held and never half-applied.
            Assert.Equal(HttpStatusCode.OK,
                (await http.GetAsync("/api/projects/alpha/files?glob=*", Ct)).StatusCode);
            string search = await http.GetStringAsync("/api/projects/alpha/search?q=class", Ct);
            Assert.Contains("src/A.cs", search, StringComparison.Ordinal);
            Assert.DoesNotContain("src/B.cs", search, StringComparison.Ordinal);
        }

        await host.WaitForRefreshesAsync();
        // And afterwards the same call sees the new index, so the answer above was the live one rather
        // than a cache that would have read the same either way.
        Assert.Contains("src/B.cs", await http.GetStringAsync("/api/projects/alpha/search?q=class", Ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_held_across_a_swap_cannot_be_used_after_it()
    {
        // Zero drain: the swap gives up waiting at once, which is the hard timeout the ticket asks
        // for and the only way to produce a connection that outlived its catalog on purpose.
        using var host = new TestHost(SearchEngine.Substring, 0);
        await host.IndexedProjectAsync("alpha", Fixture());

        using var stale = await host.OpenIndexAsync("alpha");
        await host.RefreshAsync("alpha");

        // ADR-0003: DETACH never blocks, so a connection that held USE across the swap is bound to a
        // catalog that no longer exists. This is why a lease is taken per unit of work and never kept.
        using var command = stale.Connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM files";
        var failure = await Assert.ThrowsAsync<DuckDBException>(() => command.ExecuteScalarAsync(Ct));
        Assert.Contains("alpha", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_refresh_of_the_same_project_is_refused_while_one_runs()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());

        using (var inFlight = await _host.OpenIndexAsync("alpha"))
        {
            using (var first = await _host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            await WaitForSwapAsync(_host, "alpha");

            using var second = await _host.RequestRefreshAsync("alpha");

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            string message = await ErrorAsync(second);
            Assert.Contains("already running", message, StringComparison.Ordinal);
            Assert.Contains("/refresh", message, StringComparison.Ordinal);
        }

        await _host.WaitForRefreshesAsync();
    }

    [Fact]
    public async Task A_refresh_of_another_project_queues_behind_the_one_running()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        await _host.IndexedProjectAsync("beta", Fixture("two"));

        using (var inFlight = await _host.OpenIndexAsync("alpha"))
        {
            using (var first = await _host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            await WaitForSwapAsync(_host, "alpha");

            // Accepted rather than refused: one rebuild at a time is a queue, so a second project
            // waits its turn instead of being turned away for something it has nothing to do with.
            using var second = await _host.RequestRefreshAsync("beta");
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
            Assert.Equal(RefreshState.Queued, (await _host.RefreshStatusAsync("beta")).State);
        }

        await _host.WaitForRefreshesAsync();
        Assert.Equal(RefreshState.Succeeded, (await _host.RefreshStatusAsync("beta")).State);
    }

    [Fact]
    public async Task A_refresh_is_refused_when_the_disk_would_not_hold_the_shadow_index()
    {
        // More free space than any disk has, so the guard fires on a machine of any size.
        using var host = new TestHost(SearchEngine.Substring, minimumFreeBytes: long.MaxValue);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "one",
            host.CreateGitRepository("one", new Dictionary<string, string> { [OldFile] = "class A;\n" }));

        using var response = await host.RequestRefreshAsync("alpha");

        // 507 and not the 409 a queued refresh gets: a cron reading only the status line still learns
        // that this is about storage and not about another rebuild holding the slot.
        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        string message = await ErrorAsync(response);
        Assert.Contains("MiB", message, StringComparison.Ordinal);
        Assert.Contains("Delete a project", message, StringComparison.Ordinal);
        // Refused means nothing ran: no index file, and nothing queued that could write one later.
        Assert.False(host.Indexes.HasIndex("alpha"));
        Assert.Equal(RefreshState.NeverRun, (await host.RefreshStatusAsync("alpha")).State);
    }

    [Fact]
    public async Task An_http_caller_drives_a_refresh_to_completion_by_polling_the_status()
    {
        // What the Container Apps Job on the cron does, and what the web UI does: one call to start,
        // then the status endpoint until it settles. No UI, no session, no second mechanism.
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one",
            _host.CreateGitRepository("one", new Dictionary<string, string> { [OldFile] = "class A;\n" }));

        using var response = await _host.RequestRefreshAsync("alpha");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/api/projects/alpha/refresh", response.Headers.Location?.ToString());

        var status = await PollAsync(_host, "alpha",
            s => s.State is RefreshState.Succeeded or RefreshState.Failed);

        Assert.Equal(RefreshState.Succeeded, status.State);
        Assert.Null(status.Error);
        Assert.NotNull(status.Summary);
        Assert.Equal(1, status.Summary.Files);
        Assert.NotNull(status.FinishedAt);
    }

    [Fact]
    public async Task The_status_of_a_project_that_never_refreshed_is_an_answer_and_an_unknown_one_is_404()
    {
        await _host.CreateProjectAsync("alpha");

        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.NeverRun, status.State);
        Assert.Null(status.StartedAt);

        using var http = _host.Factory.CreateClient();
        using var missing = await http.GetAsync("/api/projects/nope/refresh", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_refresh_that_can_read_no_repository_fails_and_leaves_the_old_index_serving()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        // The remote is gone, which is what a deleted or renamed repository, or an unreachable host,
        // looks like from here.
        _host.RemoveGitRepository("one");

        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _host.WaitForRefreshesAsync();

        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.Contains("one", status.Error, StringComparison.Ordinal);
        // The point of failing rather than swapping: what was searchable before still is.
        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    private static Dictionary<string, Dictionary<string, string>> Fixture(string repository = "one") =>
        new() { [repository] = new Dictionary<string, string> { [OldFile] = "class A;\n" } };

    private const string PathQuery = "SELECT qualified_path FROM files ORDER BY qualified_path";

    /// <summary>
    ///     Waits until the shadow index is built and only the swap is left, which is where the drain
    ///     holds it. The phase is compared against the constant the builder reports, not a literal, so
    ///     rewording the operator-facing sentence cannot silently turn this into a wait that never ends.
    /// </summary>
    private static Task<RefreshStatus> WaitForSwapAsync(TestHost host, string project) =>
        PollAsync(host, project, s => s.Phase == ProjectRefresh.SwapPhase);

    /// <summary>
    ///     Polls the status endpoint the way the web UI does, until it says what the test is waiting
    ///     for. The test's own cancellation token is the timeout, so a refresh that never gets there
    ///     fails the run rather than hanging it.
    /// </summary>
    private static async Task<RefreshStatus> PollAsync(TestHost host, string project,
        Func<RefreshStatus, bool> settled)
    {
        while (true)
        {
            var status = await host.RefreshStatusAsync(project);
            if (settled(status)) return status;
            await Task.Delay(10, Ct);
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct);
        Assert.NotNull(body);
        return body["error"];
    }
}
