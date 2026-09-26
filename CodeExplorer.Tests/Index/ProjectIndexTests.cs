using System.Net;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A project is ingested from its bare clones into its own DuckDB file (ADR-0003). Every test
///     pins the search engine, because <c>INSTALL fts</c> fails silently offline and the two engines
///     produce different schemas; an unpinned suite would prove different things on different machines.
///     The engine is therefore per test rather than per class, which is why the host is started in the
///     body instead of in a field.
/// </summary>
public sealed class ProjectIndexTests : IDisposable
{
    private TestHost? _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host?.Dispose();

    [Fact]
    public async Task Two_repositories_land_in_one_project_file_with_distinct_qualified_paths()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/index.ts"] = "export const one = 1;\nconsole.log(one);",
                ["README.md"] = "first"
            },
            ["two"] = new()
            {
                ["src/index.ts"] = "export const two = 2;",
                ["logo.png"] = "PNG\0\0binary"
            }
        });

        Assert.True(File.Exists(host.IndexFile("alpha")));

        var qualified = await host.ScalarsAsync("alpha", "SELECT qualified_path FROM files ORDER BY qualified_path");
        Assert.Equal(["one/README.md", "one/src/index.ts", "two/logo.png", "two/src/index.ts"], qualified);

        // A committed binary is still a file the tree knows about, only its lines are absent.
        var skipped = await host.ScalarsAsync("alpha", "SELECT skip_reason FROM files WHERE skip_reason IS NOT NULL");
        Assert.Equal(["binary"], skipped);

        // Paths stay repository-relative and repo_id scopes them, so the same path exists twice.
        var relative = await host.ScalarsAsync("alpha", """
                                                        SELECT f.path FROM files f JOIN repositories r USING (repo_id)
                                                        WHERE r.slug = 'two' AND f.skip_reason IS NULL
                                                        """);
        Assert.Equal(["src/index.ts"], relative);

        var second = await host.ScalarsAsync("alpha",
            "SELECT content FROM lines l JOIN files f USING (file_id) WHERE f.qualified_path = 'one/src/index.ts' AND l.line_number = 2");
        Assert.Equal(["console.log(one);"], second);
    }

    /// <summary>
    ///     The size is the blob's byte count, which is not its character count once a file holds more
    ///     than ASCII, and it is what both skip reasons are decided on: a binary is sized too, and a
    ///     text file one byte over the limit is refused by it. A binary over the limit is refused by its
    ///     size too, because the size is asked first: it is read off the object header, and the binary
    ///     test would inflate the whole blob (#230).
    /// </summary>
    [Fact]
    public async Task A_file_is_sized_in_bytes_and_skipped_by_its_size_or_content()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["main"] = new()
            {
                ["a.cs"] = "// café\nclass A {}\n",
                ["logo.png"] = "PNG\0\0binary",
                ["dump.sql"] = new string('x', 25 * 1024 * 1024 + 1),
                ["backup.bak"] = "\0\0" + new string('x', 25 * 1024 * 1024)
            }
        });

        var files = await host.ScalarsAsync("alpha",
            "SELECT path || '|' || size_bytes || '|' || coalesce(skip_reason, '') FROM files ORDER BY path");
        Assert.Equal([
            "a.cs|20|", "backup.bak|26214402|larger than 25 MiB", "dump.sql|26214401|larger than 25 MiB",
            "logo.png|11|binary"
        ], files);

        // Skipped files count towards the repository's bytes: 20 + 26214402 + 26214401 + 11.
        var bytes = await host.ScalarsAsync("alpha", "SELECT byte_count::VARCHAR FROM repositories");
        Assert.Equal(["52428834"], bytes);
    }

    [Fact]
    public async Task A_refresh_reports_what_it_indexed()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "one",
            host.CreateGitRepository("one", new Dictionary<string, string>
            {
                ["src/index.ts"] = "export const one = 1;\nconsole.log(one);",
                ["README.md"] = "first"
            }));

        var summary = await host.RefreshAsync("alpha");

        Assert.Equal(1, summary.Repositories);
        Assert.Equal(2, summary.Files);
        Assert.Equal(3, summary.Lines);
        Assert.Empty(summary.Skipped);
    }

    [Fact]
    public async Task Full_text_index_answers_after_use()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["main"] = new()
            {
                ["a.cs"] = "class Alpha {}\nvoid Needle() {}",
                ["b.cs"] = "class Beta {}"
            }
        });

        var matches = await host.ScalarsAsync("alpha", """
                                                       SELECT content FROM lines
                                                       WHERE fts_main_lines.match_bm25(line_id, 'needle') IS NOT NULL
                                                       """);
        Assert.Equal(["void Needle() {}"], matches);
    }

    [Fact]
    public async Task Concurrent_connections_to_different_projects_do_not_interfere()
    {
        var host = Start(SearchEngine.Fts);
        // Both repositories are called "main" inside their project, so their fixtures need names of
        // their own: a fixture is keyed by name on disk and one shared by two projects is one fixture.
        await host.CreateProjectAsync("alpha");
        await host.CreateProjectAsync("beta");
        await host.AddRepositoryAsync("alpha", "main",
            host.CreateGitRepository("a", new Dictionary<string, string> { ["only-in-alpha.txt"] = "alpha\nalpha" }));
        await host.AddRepositoryAsync("beta", "main",
            host.CreateGitRepository("b", new Dictionary<string, string> { ["only-in-beta.txt"] = "beta" }));
        await host.RefreshAsync("alpha");
        await host.RefreshAsync("beta");

        // Several threads, each with its own connection, query both projects at the same time. A
        // connection is not thread-safe, so every thread checks out its own; the instance is shared.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            string slug = i % 2 == 0 ? "alpha" : "beta";
            using var lease = await host.OpenIndexAsync(slug);
            var paths = new List<string>();
            for (int round = 0; round < 5; round++)
                paths.AddRange(await TestHost.ScalarsAsync(lease, "SELECT qualified_path FROM files"));
            return (slug, paths);
        })));

        foreach ((string slug, var paths) in results)
            Assert.Equal(Enumerable.Repeat($"main/only-in-{slug}.txt", 5), paths);
    }

    [Fact]
    public async Task A_project_without_an_index_opens_as_null()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");

        Assert.Null(await host.Indexes.OpenAsync("alpha", Ct));
        Assert.False(host.Indexes.HasIndex("alpha"));
    }

    /// <summary>
    ///     What a deployment leaves behind whenever <c>SchemaVersion</c> is bumped: the file of the
    ///     build before it, which the new build's statements name columns the old one never wrote.
    ///     Read anyway, that is a <c>Binder Error</c> out of the middle of a query — an exception where
    ///     CODING_STANDARDS asks for an answer, and a 500 on every page of a project that is in fact
    ///     indexed (#164). The file is refused on the way in instead, and the refusal says which of the
    ///     two reasons there is nothing to read.
    /// </summary>
    [Fact]
    public async Task An_index_an_older_schema_wrote_is_refused_rather_than_read()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/index.ts"] = "export const one = 1;" }
        });

        await host.ExecuteAsync("alpha",
            $"UPDATE index_info SET schema_version = {ProjectIndexes.SchemaVersion - 1}");
        // The verdict is remembered per attach, and this instance attached the file while it was still
        // current. A restart is what a deployment of the newer build looks like from here anyway.
        host.Restart();

        Assert.True(host.Indexes.HasIndex("alpha"));
        Assert.Null(await host.Indexes.OpenAsync("alpha", Ct));
        Assert.True(host.Indexes.SchemaOutdated("alpha"));

        // And what a reader is handed: the sentence naming the index that is there, not the one for a
        // project nobody has built.
        var readers = host.Services.GetRequiredService<IndexReaders>();
        var answer = await readers.OverIndexAsync("alpha", null,
            (_, _) => Task.FromResult<Outcome>(new Problem("the index was read")), Ct);
        Assert.Equal(new Problem(IndexReader.OutdatedIndex("alpha"), ProblemKind.NoIndex), answer);
    }

    [Fact]
    public async Task Substring_engine_builds_no_full_text_index_and_still_answers()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
            { ["main"] = new() { ["a.cs"] = "void Needle() {}" } });

        var schemas = await host.ScalarsAsync("alpha",
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'fts_main_lines'");
        Assert.Empty(schemas);
        var matches = await host.ScalarsAsync("alpha", "SELECT content FROM lines WHERE content LIKE '%Needle%'");
        Assert.Equal(["void Needle() {}"], matches);
    }

    [Fact]
    public async Task Refreshing_an_unknown_project_is_404_and_a_project_without_repositories_is_empty()
    {
        var host = Start(SearchEngine.Substring);

        using (var missing = await host.RequestRefreshAsync("nope"))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await host.CreateProjectAsync("alpha");
        var summary = await host.RefreshAsync("alpha");
        Assert.Equal(0, summary.Repositories);
        Assert.Equal(0, summary.Files);
    }

    [Fact]
    public async Task Lfs_repository_is_skipped_with_its_reason_in_the_summary()
    {
        var host = Start(SearchEngine.Substring);

        var summary = await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["big"] = new()
            {
                [".gitattributes"] = "*.bin filter=lfs diff=lfs merge=lfs -text\n",
                ["model.bin"] = "version https://git-lfs.github.com/spec/v1"
            },
            ["small"] = new() { ["a.txt"] = "a" }
        });

        Assert.Equal(1, summary.Repositories);
        Assert.Equal(1, summary.Files);
        string skipped = Assert.Single(summary.Skipped);
        Assert.Contains("big", skipped);
        Assert.Contains("LFS", skipped);
    }

    /// <summary>
    ///     Every line ending a repository can hold, pinned (#149). Ingest split content a character at
    ///     a time through a string builder and now slices on the newline, so the three cases the old
    ///     loop settled by accident are written down: a CRLF ending is dropped, a lone CR is dropped
    ///     where it stands rather than broken on, and a trailing newline ends the last line instead of
    ///     starting an empty one.
    ///     Asserted against the stored lines and not against a helper, because the licence for the
    ///     rewrite was that a file indexed before and after holds the same rows.
    /// </summary>
    [Fact]
    public async Task Ingest_reads_crlf_lone_cr_and_a_trailing_newline_the_way_it_always_has()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["windows.txt"] = "first\r\nsecond\r\n",
                // Classic Mac: no LF anywhere, so the whole file is one line with the CRs dropped.
                ["mac.txt"] = "first\rsecond\rthird",
                // A CR inside a line, and a last line with no newline after it.
                ["mixed.txt"] = "a\rb\r\nc",
                // Nothing but a carriage return: the old loop never started a line for it.
                ["stray.txt"] = "\r"
            }
        });

        Assert.Equal(["first", "second"], await LinesOfAsync(host, "one/windows.txt"));
        Assert.Equal(["firstsecondthird"], await LinesOfAsync(host, "one/mac.txt"));
        Assert.Equal(["ab", "c"], await LinesOfAsync(host, "one/mixed.txt"));
        Assert.Empty(await LinesOfAsync(host, "one/stray.txt"));

        // The line count on the file row is the same count, so the tree and the content agree.
        var counts = await host.ScalarsAsync("alpha",
            "SELECT line_count::VARCHAR FROM files ORDER BY qualified_path");
        Assert.Equal(["1", "2", "0", "2"], counts);
    }

    /// <summary>
    ///     Two projects' reads do not queue behind each other (#149). <c>ATTACH</c> belongs to the
    ///     DuckDB instance, so one semaphore serialises it across every project, and a lease took that
    ///     semaphore before it had even looked at whether the project was attached — which made every
    ///     read of every project wait on every other project's.
    ///     Asserted on the counter rather than on a stopwatch: a timing assertion on a machine running
    ///     the rest of this suite in parallel proves nothing, and the counter says the exact thing the
    ///     criterion asks for. Both projects are attached first, because the first attach of each is
    ///     the one that legitimately takes the gate.
    /// </summary>
    [Fact]
    public async Task Reads_of_an_attached_project_take_no_instance_wide_gate()
    {
        // Slugs of this test's own. A metric listener is process-wide and xunit runs classes in
        // parallel, so a count over every project would be a count of what the rest of the suite was
        // attaching at the time.
        using var probe = new TelemetryProbe(GateOne);
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync(GateOne, Repository("class Alpha;\n"));
        await host.IndexedProjectAsync(GateTwo, Repository("class Beta;\n"));
        await using var alpha = await host.ConnectAsync(GateOne);
        await using var beta = await host.ConnectAsync(GateTwo);
        await TestHost.CallAsync(alpha, "repo_info", []);
        await TestHost.CallAsync(beta, "repo_info", []);

        // Both projects are attached by now, and getting them there is what the gate is for — so the
        // count is not expected to be zero here. It is read as the baseline, and it being above zero
        // is what says the instrument below is one that fires rather than one nobody wired up.
        int gated = Gated(probe);
        Assert.True(gated > 0, "the first attach of each project should have taken the gate");

        // Through MCP, which is the layer the criterion names: a tool call is what an agent makes, and
        // it is the path that takes the lease along with everything else a call carries.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => TestHost.CallAsync(i % 2 == 0 ? alpha : beta, "repo_info", []))));

        Assert.Equal(gated, Gated(probe));
    }

    private const string GateOne = "gate-one";
    private const string GateTwo = "gate-two";

    private static int Gated(TelemetryProbe probe) =>
        probe.Measurements.Count(m => m.Instrument == Telemetry.AttachGate
                                      && m.Tags.GetValueOrDefault(Telemetry.ProjectTag) is GateOne or GateTwo);

    /// <summary>
    ///     A completed lease hands its connection back rather than closing it, so a swap has to empty the pool it
    ///     went into: the catalog those connections are bound to is detached and the file replaced
    ///     underneath them (#149). A read after the swap must see the new index, not fail on a stale
    ///     binding — which is what a pooled connection the swap forgot would give it.
    /// </summary>
    [Fact]
    public async Task A_swap_empties_the_pool_so_the_next_read_sees_the_new_index()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "one",
            host.CreateGitRepository("one", new Dictionary<string, string> { ["a.cs"] = "class Alpha;\n" }));
        await host.RefreshAsync("alpha");

        // Several reads first, so the pool holds connections bound to the index about to be replaced.
        for (int i = 0; i < 6; i++)
            Assert.Equal(["one/a.cs"], await host.ScalarsAsync("alpha", "SELECT qualified_path FROM files"));

        host.CommitToGitRepository("one", new Dictionary<string, string> { ["b.cs"] = "class Beta;\n" });
        await host.RefreshAsync("alpha");

        for (int i = 0; i < 6; i++)
            Assert.Equal(["one/a.cs", "one/b.cs"],
                await host.ScalarsAsync("alpha", "SELECT qualified_path FROM files ORDER BY qualified_path"));
    }

    /// <summary>
    ///     A status read cancelled after its lease was granted must close the connection rather than
    ///     pool it: the statement it abandoned can leave a result part-read behind, which the next
    ///     borrower would inherit (#241). The cancel is fired from the lease's own measurement, the one
    ///     moment between the lease being granted and the first statement on it, so no timing decides it.
    /// </summary>
    [Fact]
    public async Task A_status_read_cancelled_mid_read_does_not_pool_its_connection()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("status-cancel", Repository("class A;\n"));
        var pooled = await host.PooledConnectionAsync("status-cancel");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using (new TelemetryProbe("status-cancel")
               {
                   OnMeasured = measurement =>
                   {
                       if (measurement.Instrument == Telemetry.LeaseDuration) cancel.Cancel();
                   }
               })
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                host.Services.GetRequiredService<IndexReaders>().StatusAsync("status-cancel", true, cancel.Token));

        await AssertNotPooledAsync(host, "status-cancel", pooled);
    }

    /// <summary>
    ///     A lease is pooled only when its work said it completed, so one disposed without saying so —
    ///     a reader that threw, or one that never learned the rule — costs a new connection rather than
    ///     handing the next borrower one nobody vouched for (#267).
    /// </summary>
    [Fact]
    public async Task A_lease_disposed_without_completing_does_not_pool_its_connection()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("uncompleted", Repository("class A;\n"));
        DuckDBConnection unpooled;
        using (var lease = await host.OpenIndexAsync("uncompleted"))
            unpooled = lease.Connection;

        await AssertNotPooledAsync(host, "uncompleted", unpooled);
    }

    [Fact]
    public async Task A_completed_lease_returns_its_connection_for_the_next_lease()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("completed", Repository("class A;\n"));
        var pooled = await host.PooledConnectionAsync("completed");

        using var next = await host.OpenIndexAsync("completed");
        Assert.Same(pooled, next.Connection);
    }

    /// <summary>
    ///     The same for a read of the index rather than about it: an agent abandoning a search is the
    ///     ordinary way a read is cancelled, and its connection must not be the next caller's.
    /// </summary>
    [Fact]
    public async Task A_read_cancelled_mid_read_does_not_pool_its_connection()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("read-cancel", Repository("class A;\n"));
        DuckDBConnection used = null!;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Services.GetRequiredService<IndexReaders>().OverIndexAsync<int>("read-cancel", null,
                (reader, token) =>
                {
                    used = reader.Connection;
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(0);
                }, _ => -1, cancel.Token));

        await AssertNotPooledAsync(host, "read-cancel", used);
    }

    /// <summary>A read through <see cref="IndexReaders" /> that returned says so, and its connection is reused.</summary>
    [Fact]
    public async Task A_completed_status_read_pools_its_connection()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("status-done", Repository("class A;\n"));
        var pooled = await host.PooledConnectionAsync("status-done");

        Assert.NotNull(await host.Services.GetRequiredService<IndexReaders>().StatusAsync("status-done", true, Ct));

        using var next = await host.OpenIndexAsync("status-done");
        Assert.Same(pooled, next.Connection);
    }

    /// <summary>
    ///     Disposing twice releases once: a second release would count the reader out of the drain a
    ///     second time and let a swap through while another lease still holds the project.
    /// </summary>
    [Fact]
    public async Task A_double_dispose_releases_the_lease_once()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("twice", Repository("class A;\n"));
        var first = await host.OpenIndexAsync("twice");
        first.Completed();
        first.Dispose();
        first.Dispose();

        // Only one connection went back, so two leases held at once cannot both be handed it.
        using var a = await host.OpenIndexAsync("twice");
        using var b = await host.OpenIndexAsync("twice");
        Assert.NotSame(a.Connection, b.Connection);
    }

    /// <summary>Not pooled means closed, and so never the connection the next lease is handed.</summary>
    private static async Task AssertNotPooledAsync(TestHost host, string slug, DuckDBConnection connection)
    {
        using var next = await host.OpenIndexAsync(slug);
        Assert.NotSame(connection, next.Connection);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static Dictionary<string, Dictionary<string, string>> Repository(string content) =>
        new() { ["one"] = new() { ["a.cs"] = content } };

    private static Task<List<string>> LinesOfAsync(TestHost host, string qualifiedPath) =>
        host.ScalarsAsync("alpha", $"""
                                    SELECT l.content FROM lines l JOIN files f USING (file_id)
                                    WHERE f.qualified_path = '{qualifiedPath}' ORDER BY l.line_number
                                    """);

    private TestHost Start(SearchEngine engine) => _host = new TestHost(engine);
}
