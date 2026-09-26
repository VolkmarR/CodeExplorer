using System.Net;
using System.Net.Http.Json;
using CodeExplorer.Control;
using CodeExplorer.Git;
using CodeExplorer.Index;
using CodeExplorer.Operator;
using CodeExplorer.Reading;
using CodeExplorer.Refresh;
using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
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
        // drain is what makes a swap wait for one. It is never completed, so it is closed rather than
        // pooled, and releasing it must count the reader out all the same (#267).
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

        using var http = host.CreateClient();
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
    public async Task A_swapped_in_index_holds_every_row_the_shadow_committed_after_a_restart()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });
        await _host.RefreshAsync("alpha");

        // Nothing left in a log beside the file: what the shadow committed is in the file itself.
        Assert.False(File.Exists(_host.IndexFile("alpha") + ".wal"));

        // A restart drops the instance, so what is read next comes from the file on disk alone. The
        // index_info row a build writes last is what an index missing its tail loses first (#242).
        _host.Restart();
        Assert.Equal(["one/src/A.cs", "one/src/B.cs"], await _host.ScalarsAsync("alpha", PathQuery));
        Assert.Single(await _host.ScalarsAsync("alpha", "SELECT CAST(schema_version AS VARCHAR) FROM index_info"));
    }

    [Fact]
    public async Task A_swap_is_refused_when_the_shadow_still_has_a_write_ahead_log()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });

        using var straggler = await _host.OpenIndexInstanceAsync();
        using (await _host.OpenIndexAsync("alpha"))
        {
            using (var response = await _host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await WaitForSwapAsync(_host, "alpha");

            // The shadow is finished, checkpointed and waiting to be swapped in. A committed write puts
            // rows in its log after that checkpoint, and DuckDB's own fault switch makes the DETACH's
            // checkpoint stop short of emptying the log — and fail, which DuckDB reports after detaching.
            // An open transaction does not do it: DuckDB 1.5 checkpoints committed rows past a reader and
            // past an uncommitted writer alike, so this is the only way found to keep a log beside the
            // shadow. No DuckDB state found leaves a log after a DETACH that reported success.
            await straggler.ExecuteAsync("CREATE TABLE \"alpha$shadow\".main.straggler AS SELECT 1 AS one", Ct);
            await straggler.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'before_truncate'", Ct);
        }

        await _host.WaitForRefreshesAsync();
        // Instance-wide, so it goes before anything else here checkpoints the live index.
        await straggler.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'none'", Ct);

        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.Contains("'alpha'", status.Error, StringComparison.Ordinal);
        Assert.Contains("not put in place", status.Error, StringComparison.Ordinal);
        // A sentence for the operator, not DuckDB's own words or a path on the server's disk.
        Assert.DoesNotContain(_host.DataDirectory, status.Error, StringComparison.OrdinalIgnoreCase);
        // The old index is the one still serving.
        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    /// <summary>
    ///     The same refusal, seen from the refresh rather than its status: an <see cref="McpException" />
    ///     naming no server path, the shape every refusal of a swap or a restore takes (#291). Held in the
    ///     swap's report, which comes after the shadow's own checkpoint and before the file work.
    /// </summary>
    [Fact]
    public async Task A_swap_refused_for_a_write_ahead_log_throws_a_refusal_naming_no_server_path()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });

        using var straggler = await _host.OpenIndexInstanceAsync();
        McpException error;
        try
        {
            error = await Assert.ThrowsAsync<McpException>(() => RefreshHeldAtAsync(RefreshProgress.SwapPhase,
                async () =>
                {
                    await straggler.ExecuteAsync("CREATE TABLE \"alpha$shadow\".main.straggler AS SELECT 1 AS one",
                        Ct);
                    await straggler.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'before_truncate'", Ct);
                }));
        }
        finally
        {
            // Instance-wide, so it goes before anything else here checkpoints the live index.
            await straggler.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'none'", Ct);
        }

        Assert.Contains("'alpha'", error.Message, StringComparison.Ordinal);
        Assert.Contains("not put in place", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.DataDirectory, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    /// <summary>
    ///     A restore a refresh begins with on a wiped disk, refused because its file could not be written
    ///     out, reaches the status in its own words (#262, #291).
    /// </summary>
    [Fact]
    public async Task A_refused_restore_reaches_the_refresh_status_in_its_own_words()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        _host.DeleteIndexFile("alpha");

        using var straggler = await _host.OpenIndexInstanceAsync();
        await straggler.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'before_truncate'", Ct);
        try
        {
            using (var response = await _host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await _host.WaitForRefreshesAsync();
        }
        finally
        {
            // Instance-wide, so it goes before anything else here checkpoints.
            await straggler.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'none'", Ct);
        }

        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.Contains("'alpha'", status.Error, StringComparison.Ordinal);
        Assert.Contains("not put in place", status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.DataDirectory, status.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A failure nobody wrote a sentence for reaches the status as one naming the project and the
    ///     phase it failed in, and never in its own words: .NET's message for a file it could not write
    ///     names the file, and whoever reads the status cannot reach the server's disk (#262). The
    ///     original goes to the log. The durable file held open exclusively is the failure, as in the
    ///     interrupted re-store tests.
    /// </summary>
    [Fact]
    public async Task A_failure_in_its_own_words_is_reported_as_the_project_and_phase_and_logged_whole()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        string held = Path.Combine(_host.DurableIndexDirectory("alpha"), "lines.parquet");

        string error;
        await using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.None))
            error = await FailedRefreshErrorAsync();

        Assert.Contains("'alpha'", error, StringComparison.Ordinal);
        Assert.Contains(RefreshProgress.StorePhase, error, StringComparison.Ordinal);
        Assert.DoesNotContain("lines.parquet", error, StringComparison.Ordinal);
        Assert.DoesNotContain('/', error);
        Assert.DoesNotContain('\\', error);
        var logged = _host.Logs.Only(LogLevel.Error, "Refresh of project alpha failed");
        Assert.IsType<IOException>(logged.Exception, exactMatch: false);
        Assert.Contains("lines.parquet", logged.Exception.Message, StringComparison.Ordinal);
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

        using var http = _host.CreateClient();
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

        string error = await FailedRefreshErrorAsync();

        Assert.Contains("one", error, StringComparison.Ordinal);
        // The point of failing rather than swapping: what was searchable before still is.
        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    [Fact]
    public async Task A_refresh_follows_a_default_branch_renamed_upstream()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });
        // The clone's HEAD names the branch the fixture was on when it was made. Renaming it upstream
        // takes that name away, and the fetch prunes it: before #31 HEAD was left naming nothing, the
        // repository reported itself as empty, and no later refresh ever recovered it.
        _host.RenameDefaultBranch("one", "trunk");

        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(2, summary.Files);
        Assert.Equal(["one/src/A.cs", "one/src/B.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    [Fact]
    public async Task A_clone_whose_head_names_no_branch_recovers_on_the_next_refresh()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        // A clone left in the broken state by a version without the fix. Recovering it is the refresh's
        // job because the alternative is the operator editing HEAD inside the clone directory by hand.
        TestHost.BreakHead(_host.ClonePath("alpha", "one"), "gone");

        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(1, summary.Files);
        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    [Fact]
    public async Task A_remote_whose_head_is_detached_keeps_serving_the_branch_the_clone_has()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        // A detached HEAD is advertised as a commit id, which names no reference: before #260 looking
        // it up as one threw, and every refresh of the repository failed.
        TestHost.DetachHead(_host.FixtureGitPath("one"), _host.HeadOf("one"));

        // Twice, because the refresh that keeps the clone's branch must leave it as the next one needs it.
        await _host.RefreshAsync("alpha");
        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(1, summary.Files);
        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    [Fact]
    public async Task A_remote_whose_head_is_detached_still_delivers_new_commits_on_its_branch()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        string before = _host.HeadOf("one");
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });
        // Detached at the older commit, so a refresh that followed the detached HEAD would miss the new
        // file: the clone's HEAD follows its branch, and the branch has moved on.
        TestHost.DetachHead(_host.FixtureGitPath("one"), before);

        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(2, summary.Files);
        Assert.Equal(["one/src/A.cs", "one/src/B.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    [Fact]
    public async Task A_first_clone_of_a_detached_remote_follows_the_branch_at_that_commit()
    {
        await _host.CreateProjectAsync("alpha");
        string remote = _host.CreateGitRepository("one", new Dictionary<string, string> { [OldFile] = "class A;\n" });
        string branch = _host.BranchOf("one");
        string first = _host.HeadOf("one");
        TestHost.DetachHead(_host.FixtureGitPath("one"), first);
        await _host.AddRepositoryAsync("alpha", "one", remote);
        var cloned = await _host.RefreshAsync("alpha");

        // The operator reads the choice off the status, not the server's log.
        Assert.Equal(
            [$"Repository 'one': the remote's HEAD is detached, so its local copy follows branch '{branch}', whose tip is that commit."],
            cloned.Notes);

        // A push to the branch, with the remote's HEAD left detached where it was: a clone that stayed
        // on the detached commit would index the old tree on every refresh from here on (#288).
        _host.PushWhileDetached("one", branch, first, new Dictionary<string, string> { [NewFile] = "class B;\n" });

        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(2, summary.Files);
        Assert.Equal(["one/src/A.cs", "one/src/B.cs"], await _host.ScalarsAsync("alpha", PathQuery));
        // A copy already on its branch keeps it without a word, as it did before #288 (#260).
        Assert.Empty(summary.Notes);
    }

    [Theory]
    [InlineData("main", "master")]
    [InlineData("master", "alpha")]
    [InlineData("trunk", "zeta")]
    public async Task A_first_clone_of_a_detached_remote_breaks_a_tie_at_that_commit_by_the_documented_rule(
        string expected, string other)
    {
        await _host.CreateProjectAsync("alpha");
        string remote = _host.CreateGitRepository("one", new Dictionary<string, string> { [OldFile] = "class A;\n" });
        // Both branches at the one commit, beside the fixture's own branch renamed to sort last, so
        // nothing but the rule decides: main, then master, then the first by name (#288). libgit2's own
        // guess prefers master, which the first case proves is overruled.
        _host.RenameDefaultBranch("one", "zzz");
        string first = _host.HeadOf("one");
        _host.CreateBranch("one", expected);
        _host.CreateBranch("one", other);
        TestHost.DetachHead(_host.FixtureGitPath("one"), first);
        await _host.AddRepositoryAsync("alpha", "one", remote);
        await _host.RefreshAsync("alpha");

        _host.PushWhileDetached("one", expected, first, new Dictionary<string, string> { [NewFile] = "class B;\n" });

        Assert.Equal(2, (await _host.RefreshAsync("alpha")).Files);
    }

    [Fact]
    public async Task A_first_clone_of_a_remote_detached_off_every_branch_follows_its_default_named_branch()
    {
        await _host.CreateProjectAsync("alpha");
        string remote = _host.CreateGitRepository("one", new Dictionary<string, string> { [OldFile] = "class A;\n" });
        string first = _host.HeadOf("one");
        string branch = _host.BranchOf("one");
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });
        // Detached at a commit no branch ends at, so only the fallback rule can name one: the fixture's
        // branch is main or master, whichever init.defaultBranch says.
        TestHost.DetachHead(_host.FixtureGitPath("one"), first);
        await _host.AddRepositoryAsync("alpha", "one", remote);

        var cloned = await _host.RefreshAsync("alpha");

        Assert.Equal(2, cloned.Files);
        Assert.Contains($"follows branch '{branch}', because no branch ends at that commit", cloned.Notes.Single(),
            StringComparison.Ordinal);

        // And it keeps following that branch, which the detached commit never will.
        _host.PushWhileDetached("one", branch, first, new Dictionary<string, string> { ["src/C.cs"] = "class C;\n" });

        Assert.Equal(3, (await _host.RefreshAsync("alpha")).Files);
    }

    [Fact]
    public async Task A_local_copy_left_detached_settles_on_a_branch_at_the_next_refresh()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        // The state a first clone of a detached remote left a local copy in before #288, against a
        // remote still detached, so the remote's HEAD names nothing to repair it with.
        string first = _host.HeadOf("one");
        TestHost.DetachHead(_host.ClonePath("alpha", "one"), first);
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });
        TestHost.DetachHead(_host.FixtureGitPath("one"), first);

        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(2, summary.Files);
    }

    [Fact]
    public async Task A_remote_that_really_has_no_commits_is_still_reported_as_empty()
    {
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", _host.CreateEmptyGitRepository("one"));

        string error = await FailedRefreshErrorAsync();

        // Following the remote's HEAD must not turn an empty remote into a broken local copy: there is
        // no default branch to resolve because the remote advertises none, and that is an answer.
        Assert.Contains("has no commits yet", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_default_branch_that_cannot_be_resolved_names_the_repository_and_the_way_out()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        // A remote that has branches but whose own HEAD names none of them: nothing can be followed,
        // and the branch the clone was on is pruned, so its HEAD is left naming nothing either.
        _host.RenameDefaultBranch("one", "trunk");
        TestHost.BreakHead(_host.FixtureGitPath("one"), "main");

        string error = await FailedRefreshErrorAsync();

        // Not "has no commits yet": the remote is fine and the local copy is not, and the message says
        // which one is wrong and what the operator can do about it.
        Assert.DoesNotContain("has no commits yet", error, StringComparison.Ordinal);
        Assert.Contains("local copy", error, StringComparison.Ordinal);
        // The way out is one the reader can take through the product: whoever reads the status cannot
        // reach the server's disk, and a path there only discloses its layout (#232). The path goes
        // to the log, which the operator who can reach the disk does read.
        Assert.Contains("remove repository 'one' from project 'alpha'", error, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.DataDirectory, error, StringComparison.OrdinalIgnoreCase);
        _host.Logs.Only(LogLevel.Warning, _host.ClonePath("alpha", "one"));
        // The old index is still serving, as it is for any other refresh that could read nothing.
        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    /// <summary>
    ///     A clone that fails on the server's own disk is reported in libgit2's words, and libgit2 names
    ///     the directory it could not write. The reader of the status cannot reach that disk, so the
    ///     message says which repository failed and the log keeps where (#232).
    /// </summary>
    [Fact]
    public async Task A_clone_that_fails_on_the_local_disk_names_the_repository_and_not_the_path()
    {
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", _host.CreateGitRepository("one", Fixture()["one"]));
        // A file where the local copy's directory goes: libgit2 refuses to clone over it, and says where.
        string localCopy = _host.ClonePath("alpha", "one");
        Directory.CreateDirectory(Path.GetDirectoryName(localCopy)!);
        await File.WriteAllTextAsync(localCopy, "", Ct);

        string error = await FailedRefreshErrorAsync();

        Assert.Contains("Cloning repository 'one'", error, StringComparison.Ordinal);
        Assert.Contains("the local copy", error, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.DataDirectory, error.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        _host.Logs.Only(LogLevel.Warning, localCopy);
    }

    /// <summary>
    ///     A half-made local copy that cannot be cleared away fails before libgit2 is asked anything, in
    ///     .NET's words, and .NET names the file it could not delete. The same rule holds: the
    ///     repository in the message, the path in the log (#232). Windows only, because a file held
    ///     open blocks its delete there and nowhere else.
    /// </summary>
    [Fact]
    public async Task A_local_copy_that_cannot_be_cleared_names_the_repository_and_not_the_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file held open.");
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", _host.CreateGitRepository("one", Fixture()["one"]));
        // Not a repository, so the refresh clones afresh and has to clear the folder first.
        string localCopy = _host.ClonePath("alpha", "one");
        Directory.CreateDirectory(localCopy);
        string held = Path.Combine(localCopy, "held");
        await using var hold = new FileStream(held, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        string error = await FailedRefreshErrorAsync();

        Assert.Contains("repository 'one'", error, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.DataDirectory, error.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        _host.Logs.Only(LogLevel.Warning, localCopy);
    }

    /// <summary>
    ///     A local copy whose object store is corrupt is one repository that cannot be read, not a
    ///     project that cannot be refreshed: the others are indexed and it is named as skipped, and the
    ///     copy opened before it is released afterwards (#241). libgit2's error reaches the refresh as an
    ///     McpException since #232, so this pins the skip; the leak a failure that is not a skip would
    ///     cause is pinned by the cancellation test below.
    /// </summary>
    [Fact]
    public async Task A_corrupt_local_copy_is_skipped_and_the_copies_opened_before_it_are_released()
    {
        await ThreeRepositoriesFirstPackedAsync();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(_host.ClonePath("alpha", "two"), "objects"), "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            await File.WriteAllTextAsync(file, "not a git object", Ct);
        }

        var summary = await _host.RefreshAsync("alpha");

        Assert.Equal(2, summary.Repositories);
        Assert.Contains(summary.Skipped, reason => reason.Contains("'two'", StringComparison.Ordinal));
        await AssertReleasedAsync("one");
    }

    /// <summary>
    ///     A refresh cancelled while it fetches the second repository still releases the copy it had
    ///     already opened for the first. The cancel propagates — it is not a repository to skip — and
    ///     before #241 it escaped past the dispose, leaving the pack file of the first copy open, which
    ///     on Windows is what makes a later removal of that copy fail.
    /// </summary>
    [Fact]
    public async Task A_refresh_cancelled_mid_fetch_releases_the_copies_it_had_opened()
    {
        await ThreeRepositoriesFirstPackedAsync();
        var project = (await _host.Services.GetRequiredService<ControlDatabase>().FindAsync("alpha", Ct))!;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // Driven directly rather than through the endpoint, so the cancel lands at a known point: the
        // report that the second fetch is starting, after the first copy was opened.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _host.Services.GetRequiredService<ProjectRefresh>().RunAsync(project, progress =>
            {
                if (progress.Phase == "Fetching 'two'") cancel.Cancel();
            }, cancel.Token));

        await AssertReleasedAsync("one");
    }

    /// <summary>
    ///     A project deleted while its refresh is between the ingest and the swap
    ///     (GHSA-253f-grfp-cqq7). The refresh checked that the project existed only before it started, so
    ///     it went on to store a durable copy and swap its shadow in, and a project later created under
    ///     the same slug opened the deleted one's files. The refresh is held in a report while the delete
    ///     runs to completion: the one that the store is starting — the shadow is built and nothing has
    ///     been stored — and the one before the fetch, after which the refresh clones again what the
    ///     delete removed.
    /// </summary>
    [Theory]
    [InlineData(RefreshProgress.StorePhase)]
    [InlineData("Fetching 'one'")]
    public async Task A_project_deleted_mid_refresh_gets_nothing_back(string phase)
    {
        await _host.IndexedProjectAsync("alpha", Fixture());

        await RefreshDeletedAtAsync(phase, async () =>
        {
            using var http = _host.CreateClient();
            using var deleted = await http.DeleteAsync("/api/projects/alpha", Ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        });

        Assert.False(File.Exists(_host.IndexFile("alpha")));
        Assert.False(Directory.Exists(_host.DurableIndexDirectory("alpha")));
        Assert.False(Directory.Exists(_host.ProjectClones("alpha")));

        // The slug reused: the new project has never been built, and must not open the old one's files.
        await _host.CreateProjectAsync("alpha");
        using var reader = _host.CreateClient();
        var detail = await reader.GetFromJsonAsync<ProjectDetail>("/api/projects/alpha", Ct);
        Assert.Null(detail!.Index.BuiltAt);
        Assert.False(File.Exists(_host.IndexFile("alpha")));
    }

    /// <summary>
    ///     A delete that forgot the project in the control database and never got as far as discarding
    ///     its index — its request abandoned while it waited behind the refresh. The discard count never
    ///     moved, so only the publish asking the control database again, under the writer gate, keeps the
    ///     build out (GHSA-253f-grfp-cqq7). Asserted by a commit the build would have picked up.
    /// </summary>
    [Fact]
    public async Task A_refresh_keeps_nothing_of_a_project_the_control_database_no_longer_holds()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        _host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });

        await RefreshDeletedAtAsync(RefreshProgress.StorePhase, () =>
            _host.Services.GetRequiredService<ControlDatabase>().DeleteProjectAsync("alpha", Ct));

        Assert.Equal(["one/src/A.cs"], await _host.ScalarsAsync("alpha", PathQuery));
    }

    /// <summary>
    ///     Refreshes project alpha, runs <paramref name="delete" /> while the refresh is held in the
    ///     report of <paramref name="phase" />, and asserts the refresh then failed because the project
    ///     was deleted.
    /// </summary>
    private async Task RefreshDeletedAtAsync(string phase, Func<Task> delete)
    {
        var error = await Assert.ThrowsAsync<ExplainedFailureException>(() => RefreshHeldAtAsync(phase, delete));
        Assert.Contains("deleted", error.Message);
    }

    /// <summary>
    ///     Refreshes project alpha and runs <paramref name="whileHeld" /> while the refresh is held in the
    ///     report of <paramref name="phase" />. Driven directly rather than through the endpoint, so the
    ///     work lands at a known point and the refresh's own exception reaches the test, not only its status.
    /// </summary>
    private async Task RefreshHeldAtAsync(string phase, Func<Task> whileHeld)
    {
        var project = (await _host.Services.GetRequiredService<ControlDatabase>().FindAsync("alpha", Ct))!;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();

        var refreshing = Task.Run(() => _host.Services.GetRequiredService<ProjectRefresh>().RunAsync(project,
            progress =>
            {
                if (progress.Phase != phase) return;
                reached.TrySetResult();
                // A blocking wait, because the report is synchronous by design and is the one point a
                // test can stop a refresh at. It blocks a pool thread, never a task.
                resume.Wait(Ct);
            }, Ct), Ct);

        await reached.Task.WaitAsync(Ct);
        try
        {
            await whileHeld();
        }
        finally
        {
            // Released even when the work failed, so a failing test does not leave a refresh blocked.
            resume.Set();
        }

        await refreshing;
    }

    /// <summary>
    ///     Project alpha indexed from three repositories, with the local copy of the first packed. A
    ///     clone of a fixture on the same disk copies its objects loose, and libgit2 closes a loose
    ///     object once read; a pack is what an open copy keeps a handle on, so without it a leaked copy
    ///     would be invisible.
    /// </summary>
    private async Task ThreeRepositoriesFirstPackedAsync()
    {
        await _host.IndexedProjectAsync("alpha",
            new() { ["one"] = Fixture("one")["one"], ["two"] = Fixture("two")["two"], ["three"] = Fixture("three")["three"] });

        string clone = _host.ClonePath("alpha", "one");
        string objects = Path.Combine(clone, "objects");
        using (var repository = new LibGit2Sharp.Repository(clone))
            repository.ObjectDatabase.Pack(new LibGit2Sharp.PackBuilderOptions(Path.Combine(objects, "pack")));
        foreach (string loose in Directory.EnumerateDirectories(objects, "??")) TestHost.DeleteTree(loose);
    }

    /// <summary>
    ///     Asserts nothing holds a file of one local copy open, then removes it. Only Windows refuses
    ///     the exclusive open while libgit2 holds a pack, so elsewhere this proves only the removal.
    /// </summary>
    private async Task AssertReleasedAsync(string repository)
    {
        foreach (string file in Directory.EnumerateFiles(_host.ClonePath("alpha", repository), "*",
                     SearchOption.AllDirectories))
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }

        await _host.Services.GetRequiredService<GitClones>().RemoveAsync("alpha", repository, Ct);
        Assert.False(Directory.Exists(_host.ClonePath("alpha", repository)));
    }

    private Task<string> FailedRefreshErrorAsync() => _host.FailedRefreshErrorAsync("alpha");

    /// <summary>
    ///     Step 3 does several separable things, and until #91 everything after the attribution ran
    ///     under its label — so an operator watching, or anyone reading the status back to find out what
    ///     a refresh cost, saw one sentence covering four pieces of work. The reports are collected from
    ///     the refresh itself rather than polled from the status endpoint: on a fixture this small a
    ///     poll would miss phases that last microseconds, and what is asserted is the order they were
    ///     reported in, which a poll cannot see at all.
    ///     Both engines, because the full-text phase is the one piece that is not always there: a
    ///     project searched by substring scan must not report a phase it spends no time in (#91).
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Substring)]
    [InlineData(SearchEngine.Fts)]
    public async Task Each_piece_of_a_build_reports_its_own_phase(SearchEngine engine)
    {
        using var host = new TestHost(engine);
        await host.IndexedProjectAsync("alpha", Fixture());
        var project = await host.Services.GetRequiredService<Control.ControlDatabase>().FindAsync("alpha", Ct);
        Assert.NotNull(project);

        // The refresh is run directly rather than through the service, which reports into a status that
        // keeps only the latest phase; this is the same code path with the reports kept.
        var reported = new List<RefreshProgress>();
        await host.Services.GetRequiredService<ProjectRefresh>().RunAsync(project, reported.Add, Ct);

        List<string> expected =
            [RefreshProgress.IngestPhase, RefreshProgress.ResolveImportsPhase, RefreshProgress.AttributionPhase,
                RefreshProgress.OverviewPhase];
        if (engine == SearchEngine.Fts) expected.Add(RefreshProgress.FullTextPhase);
        expected.AddRange([RefreshProgress.StorePhase, RefreshProgress.SwapPhase]);
        // Only the fixed phases: the counting ones in between name the repository they are working on,
        // and this is about which pieces of work are named and in what order, not how often they report.
        Assert.Equal(expected, reported.Select(progress => progress.Phase).Where(Fixed).ToList());

        // Every report counts against the same total, and each fixed phase runs at the step that owns
        // it: the overview and the full-text index reported as step 3 alongside the history until they
        // were split out of it, which is what had the counter sit on 3 for most of a large refresh.
        Assert.All(reported, progress => Assert.Equal(RefreshProgress.TotalStepCount, progress.TotalSteps));
        Assert.All(reported.Where(progress => StepOf(progress.Phase) is not null),
            progress => Assert.Equal(StepOf(progress.Phase), progress.Step));

        // And they are reported in step order, never a step the refresh has already left.
        var steps = reported.Select(progress => progress.Step).ToList();
        Assert.Equal(steps.Order(), steps);
    }

    /// <summary>
    ///     #91 named each piece of step 3 while it ran, and a refresh that had finished still could not
    ///     say what it spent: the status keeps one phase at a time, so the whole timeline collapsed to
    ///     "Done" the moment the swap returned. Attributing one real refresh therefore still took a
    ///     bespoke poller — 25 ms against a live server, to catch phases lasting a few milliseconds —
    ///     which is the harness #91's last item set out to make unnecessary, and is what #92 means by
    ///     making the status honest about a cost the project has decided to accept.
    ///     Read back from the finished status alone, with nothing polled while it ran, because that is
    ///     the whole claim. Both engines for the reason the phase test covers both: the full-text
    ///     rebuild is the item #92 is about and the one item that is not always there.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Substring)]
    [InlineData(SearchEngine.Fts)]
    public async Task A_finished_refresh_says_what_each_phase_cost(SearchEngine engine)
    {
        using var host = new TestHost(engine);
        await host.IndexedProjectAsync("alpha", Fixture());
        host.CommitToGitRepository("one", new Dictionary<string, string> { [NewFile] = "class B;\n" });

        await host.RefreshAsync("alpha");
        var status = await host.RefreshStatusAsync("alpha");

        List<string> expected =
            [RefreshProgress.IngestPhase, RefreshProgress.ResolveImportsPhase, RefreshProgress.AttributionPhase,
                RefreshProgress.OverviewPhase];
        if (engine == SearchEngine.Fts) expected.Add(RefreshProgress.FullTextPhase);
        expected.AddRange([RefreshProgress.StorePhase, RefreshProgress.SwapPhase]);
        // The same phases the reports carry, in the same order, and now outliving the refresh that
        // reported them. Only the fixed ones, for the reason the report test gives: the counting
        // phases name the repository they are working on.
        Assert.Equal(expected, status.Phases.Select(cost => cost.Phase).Where(Fixed).ToList());

        // Each phase carries the step it ran at, so a reader can group the timeline the way the
        // counter counted rather than having to know which phase belongs to which step (#91).
        Assert.All(status.Phases.Where(cost => StepOf(cost.Phase) is not null),
            cost => Assert.Equal(StepOf(cost.Phase), cost.Step));

        // The timeline accounts for the refresh rather than merely listing it: every phase costs
        // something a clock could measure, and together they fit inside the wall clock the status
        // already reports. Fitting inside it and not equalling it — the queue wait before the first
        // phase is part of that span and is not a phase.
        Assert.All(status.Phases, cost => Assert.True(cost.Seconds >= 0, $"{cost.Phase} cost {cost.Seconds}s"));
        Assert.NotNull(status.FinishedAt);
        Assert.NotNull(status.StartedAt);
        var wall = (status.FinishedAt.Value - status.StartedAt.Value).TotalSeconds;
        var attributed = status.Phases.Sum(cost => cost.Seconds);
        Assert.True(attributed <= wall + 0.01, $"phases sum to {attributed}s of a {wall}s refresh");
    }

    /// <summary>
    ///     A refresh that died says how far it got. The failure replaces the status wholesale — a
    ///     failed refresh has no summary and no progress — and the timeline is the one part of it worth
    ///     carrying across, because which phase a refresh failed in is most of what an operator wants
    ///     from one that failed.
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_keeps_the_phases_it_got_through()
    {
        await _host.IndexedProjectAsync("alpha", Fixture());
        // The same break the message test uses: no branch is left to follow, so the refresh fails
        // partway through rather than being refused before it starts and reporting nothing at all.
        _host.RenameDefaultBranch("one", "trunk");
        TestHost.BreakHead(_host.FixtureGitPath("one"), "main");

        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _host.WaitForRefreshesAsync();

        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.NotEmpty(status.Phases);
    }

    /// <summary>The phases with a wording of their own, as opposed to the counting ones naming a repository.</summary>
    private static bool Fixed(string phase) =>
        phase is RefreshProgress.IngestPhase or RefreshProgress.ResolveImportsPhase
            or RefreshProgress.AttributionPhase
            or RefreshProgress.OverviewPhase or RefreshProgress.FullTextPhase or RefreshProgress.StorePhase
            or RefreshProgress.SwapPhase;

    /// <summary>
    ///     Which step owns a phase with a wording of its own, and null for the counting phases that name
    ///     a repository — those belong to a step too, but only their fixed siblings pin the numbering.
    ///     Spelled out here rather than derived from the constants, so that a renumbering has to be
    ///     written down twice and cannot pass by agreeing with itself.
    /// </summary>
    private static int? StepOf(string phase) => phase switch
    {
        RefreshProgress.StartPhase => 1,
        RefreshProgress.IngestPhase => 2,
        // The reading step's second phase: the names were appended by the walk and resolve once the
        // whole project is in the shadow, so it finishes that step rather than opening a new one.
        RefreshProgress.ResolveImportsPhase => 2,
        RefreshProgress.AttributionPhase => 3,
        RefreshProgress.OverviewPhase => 4,
        RefreshProgress.FullTextPhase => 5,
        RefreshProgress.StorePhase => 6,
        RefreshProgress.SwapPhase => 7,
        _ => null
    };

    private static Dictionary<string, Dictionary<string, string>> Fixture(string repository = "one") =>
        new() { [repository] = new Dictionary<string, string> { [OldFile] = "class A;\n" } };

    private const string PathQuery = "SELECT qualified_path FROM files ORDER BY qualified_path";

    /// <summary>
    ///     Waits until the shadow index is built and only the swap is left, which is where the drain
    ///     holds it. The phase is compared against the constant the builder reports, not a literal, so
    ///     rewording the operator-facing sentence cannot silently turn this into a wait that never ends.
    /// </summary>
    private static Task<RefreshStatus> WaitForSwapAsync(TestHost host, string project) =>
        PollAsync(host, project, s => s.Phase == RefreshProgress.SwapPhase);

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
