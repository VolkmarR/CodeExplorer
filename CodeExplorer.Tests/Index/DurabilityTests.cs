using System.Net;
using CodeExplorer.Control;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using CodeExplorer.Operator;
using CodeExplorer.Reading;
using CodeExplorer.Refresh;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What survives the container's disk being wiped (#9): a project's index as a Parquet set under
///     its own prefix, and the control database as a file under its own. Every test here runs against
///     the folder store, which is what an app with no blob account configured uses (ADR-0004), and
///     never against a real account.
///     A wiped disk is a deleted file and a restarted host, because that is all a scale to zero leaves
///     behind: the local files are gone and the durable copy is not.
/// </summary>
public sealed class DurabilityTests : IDisposable
{
    private TestHost? _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host?.Dispose();

    [Fact]
    public async Task Indexing_stores_every_table_under_the_projects_own_prefix()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));

        // Read off the index the build just created rather than off the table list the store walks, so
        // a table added to the DDL and not to that list fails here instead of on the next scale to zero.
        // The history tables are as much of the index as the code ones are (ADR-0007), and that
        // includes the rename chains the build derives from them (#148): a restore does not re-walk.
        Assert.Equal(await IndexTableFilesAsync(host, "alpha"), StoredFileNames(host, "alpha"));
    }

    [Fact]
    public async Task Deleting_the_index_file_and_reconnecting_restores_it_from_parquet()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\nclass Beta;\n"));
        host.DeleteIndexFile("alpha");
        Assert.False(host.Indexes.HasIndex("alpha"));

        // Through the ordinary read path and not through a restore method: "reconnecting restores it"
        // is the acceptance criterion, and a caller that had to ask for the restore would not meet it.
        var lines = await host.ScalarsAsync("alpha", "SELECT content FROM lines ORDER BY line_id");

        Assert.Equal(["class Alpha;", "class Beta;"], lines);
        Assert.True(host.Indexes.HasIndex("alpha"));
    }

    /// <summary>
    ///     A restore whose file cannot be written out is refused to the agent that caused it in words it
    ///     can act on (#291). The SDK reports any other exception from a tool as a generic error, so the
    ///     sentence arriving at all is what shows the refusal is an <see cref="McpException" />. DuckDB's
    ///     fault switch is what makes the restore's checkpoint fail, as in the swap's refusal test.
    /// </summary>
    [Fact]
    public async Task A_restore_that_cannot_write_its_file_out_reaches_an_agent_as_a_refusal()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");

        McpException thrown;
        await using (await host.FailingCheckpointsAsync())
        {
            await using var client = await host.ConnectAsync("alpha");
            thrown = await Assert.ThrowsAsync<McpException>(() =>
                TestHost.CallAsync(client, "project_overview", []));
        }

        Assert.Contains("'alpha'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("not put in place", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.DataDirectory, thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(host.Indexes.HasIndex("alpha"));

        // The remedy the sentence names: the next read tries the restore again.
        Assert.Equal(["class Alpha;"], await host.ScalarsAsync("alpha", "SELECT content FROM lines"));
    }

    /// <summary>
    ///     A restore that fails for any other reason also reaches the agent as a sentence with a remedy,
    ///     not as the SDK's generic error (#291). What .NET or DuckDB said names files on the server, so
    ///     it goes to the log only. The failure is the lines table held open exclusively, as in the
    ///     refresh's own restore test.
    /// </summary>
    [Fact]
    public async Task A_restore_that_fails_under_an_agent_reaches_it_as_a_refusal_naming_no_server_path()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();

        McpException thrown;
        await using (File.Open(Path.Combine(host.DurableIndexDirectory("alpha"), "lines.parquet"),
                         FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await using var client = await host.ConnectAsync("alpha");
            thrown = await Assert.ThrowsAsync<McpException>(() =>
                TestHost.CallAsync(client, "project_overview", []));
        }

        Assert.Contains("'alpha'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("could not be restored", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.DataDirectory, thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_restored_index_still_answers_full_text_searches()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["a.cs"] = "class Alpha {}\nvoid Needle() {}" } });
        host.DeleteIndexFile("alpha");

        // The BM25 index is fts's own tables and is not in the Parquet, so this is the assertion that
        // a restore rebuilds it rather than leaving the project on a substring scan it did not choose.
        var matches = await host.ScalarsAsync("alpha", """
                                                       SELECT content FROM lines
                                                       WHERE fts_main_lines.match_bm25(line_id, 'needle') IS NOT NULL
                                                       """);

        Assert.Equal(["void Needle() {}"], matches);
        var info = await host.ScalarsAsync("alpha", "SELECT fts_indexed::VARCHAR AS indexed FROM index_info");
        Assert.Equal(["true"], info);
    }

    [Fact]
    public async Task A_restore_keeps_the_build_time_and_how_the_project_names_its_files()
    {
        var host = Start(SearchEngine.Substring);
        // Single-repository, because single_repository is the one column a read path cannot recover
        // from anywhere else: a qualified path cannot be parsed without it (ADR-0006).
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"), true);
        var before = await host.GetJsonAsync<ProjectDetail>("/api/projects/alpha");
        host.DeleteIndexFile("alpha");

        var after = await host.GetJsonAsync<ProjectDetail>("/api/projects/alpha");

        Assert.Equal(before.Index.BuiltAt, after.Index.BuiltAt);
        Assert.Equal(["src/A.cs"], await host.ScalarsAsync("alpha", "SELECT qualified_path FROM files"));
    }

    [Fact]
    public async Task A_project_attaches_on_first_connection_and_restoring_it_leaves_the_others_cold()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        await host.IndexedProjectAsync("beta", Repository("class Beta;\n", "two"));
        host.DeleteIndexFile("alpha");
        host.DeleteIndexFile("beta");

        // A wake with an empty disk. Nothing has connected to either project, so nothing may have been
        // restored: an off-hours wake must cost one project's restore rather than everyone's.
        host.Restart();
        Assert.False(host.Indexes.HasIndex("alpha"));
        Assert.False(host.Indexes.HasIndex("beta"));

        using (var lease = await host.Indexes.OpenAsync("alpha", Ct)) Assert.NotNull(lease);

        Assert.True(host.Indexes.HasIndex("alpha"));
        Assert.False(host.Indexes.HasIndex("beta"));
    }

    [Fact]
    public async Task Two_projects_restore_at_the_same_time_without_waiting_for_each_other()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        await host.IndexedProjectAsync("beta", Repository("class Beta;\n", "two"));
        host.DeleteIndexFile("alpha");
        host.DeleteIndexFile("beta");
        host.Restart();

        // The writer gate a restore holds is per project, so these two do not serialise on each other.
        // Both answering is the assertion: one shared gate would still pass, but a deadlock or a
        // restore overwriting the other project's file would not.
        var restored = await Task.WhenAll(
            Task.Run(() => host.ScalarsAsync("alpha", "SELECT content FROM lines"), Ct),
            Task.Run(() => host.ScalarsAsync("beta", "SELECT content FROM lines"), Ct));

        Assert.Equal(["class Alpha;"], restored[0]);
        Assert.Equal(["class Beta;"], restored[1]);
    }

    [Fact]
    public async Task A_connection_arriving_while_a_refresh_swaps_does_not_restore_over_the_new_index()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.CommitToGitRepository("one", new Dictionary<string, string> { ["src/A.cs"] = "class Rebuilt;\n" });

        // A refresh and a reader at once. The reader's lazy restore looks for the file outside the swap
        // gate, so it can arrive in the moment the swap has moved the old file away; the durable copy it
        // would load is the older one, and loading it would undo the refresh.
        var refreshing = host.RefreshAsync("alpha");
        var reading = Task.Run(() => host.ScalarsAsync("alpha", "SELECT content FROM lines"), Ct);
        await Task.WhenAll(refreshing, reading);

        Assert.Equal(["class Rebuilt;"], await host.ScalarsAsync("alpha", "SELECT content FROM lines"));
    }

    /// <summary>
    ///     A refresh that is the first thing a wiped replica does (#229): the cron arrives before any agent
    ///     has connected, so there is no local file to carry history over from and only the durable copy
    ///     knows it. Asserted with a commit no walk would produce, planted and stored before the wipe: a
    ///     refresh that restored first keeps it, and one that re-walked from the root cannot.
    /// </summary>
    [Fact]
    public async Task A_refresh_on_a_wiped_disk_carries_history_over_from_the_durable_copy()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        // Id 0, below every real one, for the reason HistoryTests gives: a ghost planted as the newest
        // commit would read as a rewrite of HEAD's line and be re-imported rather than carried.
        await host.ExecuteAsync("alpha", "INSERT INTO commits VALUES (0, 'one', 'deadbeef', 'Ghost', "
                                         + "'ghost@example.invalid', now(), 'Known only to the index', '')");
        // A refresh carries the ghost into the shadow it builds, and so into the durable copy it stores.
        await host.RefreshAsync("alpha");
        const string sql = "SELECT commit_id || ' ' || subject FROM commits ORDER BY commit_id";
        var before = await host.ScalarsAsync("alpha", sql);

        host.DeleteIndexFile("alpha");
        host.Restart();
        host.CommitToGitRepository("one", new Dictionary<string, string> { ["src/A.cs"] = "class Alpha2;\n" });
        await host.RefreshAsync("alpha");

        // Every commit the durable copy held, under the id it had, and the new one appended after them.
        var after = await host.ScalarsAsync("alpha", sql);
        Assert.Equal(before, after.Take(before.Count));
        Assert.Equal(before.Count + 1, after.Count);
    }

    /// <summary>
    ///     A refresh on a wiped disk restores first, and the shadow it then builds replaces the restored
    ///     index within minutes (#290). The restore used to build the full-text index for it anyway, so
    ///     the most expensive phase of a large refresh was paid twice. Read off the phases the status
    ///     records, which name every full-text build a refresh makes, the restore's included.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_begins_with_a_restore_builds_the_full_text_index_once()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha {}\nvoid Needle() {}\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();

        await host.RefreshAsync("alpha");

        var phases = (await host.RefreshStatusAsync("alpha")).Phases.Select(cost => cost.Phase).ToList();
        Assert.Single(phases, phase => phase == RefreshProgress.FullTextPhase);
        // The restore is a phase of its own, costed like the rest, and it runs before the first fetch.
        Assert.Equal(1, phases.IndexOf(RefreshProgress.RestorePhase));
        Assert.Equal(RefreshProgress.StartPhase, phases[0]);
        // And the index the refresh left is searched by BM25, which the shadow built.
        Assert.Equal(["true"], await host.ScalarsAsync("alpha", "SELECT fts_indexed::VARCHAR FROM index_info"));
    }

    /// <summary>
    ///     The restore a refresh begins with skips the full-text build because the shadow replaces it.
    ///     A refresh that then fails has no shadow to swap in, and the restored index would be searched by
    ///     substring scan until a later refresh succeeded — on a disk that is not wiped, indefinitely.
    ///     The failure is a fetch with no branch left to follow, so every repository fails after the restore.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_fails_after_its_restore_leaves_a_full_text_index()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha {}\nvoid Needle() {}\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();
        host.RenameDefaultBranch("one", "trunk");
        GitFixtures.BreakHead(host.FixtureGitPath("one"), "main");

        using (var response = await host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await host.WaitForRefreshesAsync();

        Assert.Equal(RefreshState.Failed, (await host.RefreshStatusAsync("alpha")).State);
        Assert.Equal(["true"], await host.ScalarsAsync("alpha", "SELECT fts_indexed::VARCHAR FROM index_info"));
        Assert.Equal(["void Needle() {}"], await host.ScalarsAsync("alpha", """
                                                                           SELECT content FROM lines
                                                                           WHERE fts_main_lines.match_bm25(line_id, 'needle') IS NOT NULL
                                                                           """));
    }

    /// <summary>
    ///     The settle that gives such an index its full-text index is the third path that puts a file in
    ///     place, and the one whose refusal no refresh reaches on its own: it would need the checkpoint to
    ///     fail after the restore's and not before it. Called directly for that, with DuckDB's fault switch
    ///     on for the settle alone. The refusal is the settle's own sentence, and the live file is still
    ///     on disk.
    /// </summary>
    [Fact]
    public async Task A_settle_that_cannot_write_its_file_out_is_refused_in_its_own_words()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha {}\nvoid Needle() {}\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();
        await host.Indexes.RestoreForRefreshAsync("alpha", static _ => { }, Ct);

        ExplainedFailureException thrown;
        await using (await host.FailingCheckpointsAsync())
            thrown = await Assert.ThrowsAsync<ExplainedFailureException>(() =>
                host.Indexes.SettleRestoreAsync("alpha", Ct));

        Assert.Contains("'alpha'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("still serves", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.DataDirectory, thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(host.Indexes.HasIndex("alpha"));
    }

    /// <summary>
    ///     A refused settle leaves nothing of itself behind (#349). Its checkpoint failing invalidates the
    ///     restore catalog, and while that stayed attached every <c>duckdb_tables()</c> on the instance
    ///     threw, so the first open of any project failed. <c>beta</c> is opened for the first time after
    ///     the refusal, which is what asks that question; <c>alpha</c> is still served by its own file,
    ///     without a full-text index, which is what the refusal's remedy promises.
    /// </summary>
    [Fact]
    public async Task A_refused_settle_leaves_the_project_and_every_other_one_readable()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha {}\n"));
        await host.IndexedProjectAsync("beta", Repository("class Beta {}\n", "two"));
        host.DeleteIndexFile("alpha");
        host.Restart();
        await host.Indexes.RestoreForRefreshAsync("alpha", static _ => { }, Ct);

        await using (await host.FailingCheckpointsAsync())
            await Assert.ThrowsAsync<ExplainedFailureException>(() => host.Indexes.SettleRestoreAsync("alpha", Ct));

        Assert.Equal(["class Beta {}"], await host.ScalarsAsync("beta", "SELECT content FROM lines"));
        Assert.Equal(["false"], await host.ScalarsAsync("alpha", "SELECT fts_indexed::VARCHAR FROM index_info"));
        await AssertNoRestoreLeftAsync(host, "alpha");
    }

    /// <summary>
    ///     The same for a restore an agent's read started: refused, it leaves no restore catalog or file,
    ///     so another project's first open succeeds, and the next read of the project restores it.
    /// </summary>
    [Fact]
    public async Task A_refused_restore_leaves_every_other_project_readable_and_nothing_of_itself()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        await host.IndexedProjectAsync("beta", Repository("class Beta;\n", "two"));
        host.DeleteIndexFile("alpha");
        host.Restart();

        await using (await host.FailingCheckpointsAsync())
            await Assert.ThrowsAsync<McpException>(() => host.Indexes.OpenAsync("alpha", Ct));

        await AssertNoRestoreLeftAsync(host, "alpha");
        Assert.Equal(["class Beta;"], await host.ScalarsAsync("beta", "SELECT content FROM lines"));
        Assert.Equal(["class Alpha;"], await host.ScalarsAsync("alpha", "SELECT content FROM lines"));
    }

    /// <summary>
    ///     A refused settle is tried again (#349): the project stays marked as lacking its full-text
    ///     index, so the next settle builds one. Cleared before the rebuild, the mark made the refusal's
    ///     "the next refresh builds one" untrue for the rest of the process.
    /// </summary>
    [Fact]
    public async Task The_settle_after_a_refused_one_builds_the_full_text_index()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha {}\nvoid Needle() {}\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();
        await host.Indexes.RestoreForRefreshAsync("alpha", static _ => { }, Ct);
        await using (await host.FailingCheckpointsAsync())
            await Assert.ThrowsAsync<ExplainedFailureException>(() => host.Indexes.SettleRestoreAsync("alpha", Ct));

        await host.Indexes.SettleRestoreAsync("alpha", Ct);

        Assert.Equal(["true"], await host.ScalarsAsync("alpha", "SELECT fts_indexed::VARCHAR FROM index_info"));
        Assert.Equal(["void Needle() {}"], await host.ScalarsAsync("alpha", """
                                                                           SELECT content FROM lines
                                                                           WHERE fts_main_lines.match_bm25(line_id, 'needle') IS NOT NULL
                                                                           """));
    }

    /// <summary>
    ///     A settle asked for on a cancelled token does not run (#349). A refresh hands it the host's
    ///     <c>ApplicationStopping</c>, which is cancelled by the time a refresh that shutdown cut short
    ///     gets to it, and the settle is a copy and a full-text build that nothing waits for once the
    ///     host stops. So it is not started then rather than cut off part-way, it leaves no restore file,
    ///     and the project stays marked: a settle that does get to run still builds the index.
    /// </summary>
    [Fact]
    public async Task A_settle_on_a_cancelled_token_does_nothing_and_leaves_the_project_to_the_next_one()
    {
        var host = Start(SearchEngine.Fts);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha {}\nvoid Needle() {}\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();
        await host.Indexes.RestoreForRefreshAsync("alpha", static _ => { }, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Indexes.SettleRestoreAsync("alpha", new CancellationToken(true)));

        Assert.Equal(["false"], await host.ScalarsAsync("alpha", "SELECT fts_indexed::VARCHAR FROM index_info"));
        await AssertNoRestoreLeftAsync(host, "alpha");
        await host.Indexes.SettleRestoreAsync("alpha", Ct);
        Assert.Equal(["true"], await host.ScalarsAsync("alpha", "SELECT fts_indexed::VARCHAR FROM index_info"));
    }

    /// <summary>
    ///     What a refused restore or settle must not leave: its file, the log beside it, or its catalog
    ///     attached to the instance every project shares.
    /// </summary>
    private static async Task AssertNoRestoreLeftAsync(TestHost host, string slug)
    {
        string restore = Path.Combine(host.DataDirectory, "indexes", slug + ".restore.duckdb");
        Assert.False(File.Exists(restore));
        Assert.False(File.Exists(restore + ".wal"));
        await using var instance = await host.OpenIndexInstanceAsync();
        Assert.Equal(0, await instance.CountAsync("SELECT count(*) FROM duckdb_databases() WHERE database_name = $name",
            [new DuckDBParameter("name", slug + "$restore")], Ct));
    }

    /// <summary>
    ///     A restore that fails inside a refresh names the restore, not "Starting" (#290): an operator
    ///     reading the status has to be able to tell that the durable copy was what failed. The failure
    ///     is the lines table held open exclusively, so its transfer fails part-way through the fetch.
    /// </summary>
    [Fact]
    public async Task A_restore_that_fails_inside_a_refresh_is_reported_in_its_own_phase()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();

        await using (File.Open(Path.Combine(host.DurableIndexDirectory("alpha"), "lines.parquet"),
                         FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using (var response = await host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await host.WaitForRefreshesAsync();
        }

        var status = await host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.Contains($"\"{RefreshProgress.RestorePhase}\"", status.Error);
        Assert.Equal(RefreshProgress.RestorePhase, status.Phases[^1].Phase);
    }

    [Fact]
    public async Task The_project_list_reads_every_project_without_restoring_any_of_them()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();

        var projects = await host.GetJsonAsync<List<ProjectSummary>>("/api/projects");

        // The page shows the project and reports it as not built, which is what the disk says. Drawing
        // one list must not wake every durable copy, so this is the read that deliberately does not.
        Assert.Null(Assert.Single(projects).Index.BuiltAt);
        Assert.False(host.Indexes.HasIndex("alpha"));
    }

    [Fact]
    public async Task Deleting_the_control_database_and_restarting_restores_it_from_its_backup()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "one",
            host.CreateGitRepository("one", new Dictionary<string, string> { ["src/A.cs"] = "class A;\n" }));

        host.RestartWithoutControlDatabase();

        var detail = await host.GetJsonAsync<ProjectDetail>("/api/projects/alpha");
        Assert.Equal("one", Assert.Single(detail.Repositories).Slug);
        // The credential column is restored with it; what the page may say about it is still only
        // whether it is set, which is what the backup being a file rather than a report protects.
        Assert.False(Assert.Single(detail.Repositories).HasCredential);
    }

    [Fact]
    public async Task A_startup_that_migrates_the_control_database_stores_the_migrated_shape()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        // The backup an older build left: projects without single_repository, which is the column
        // ADR-0006 added. Replacing the stored copy is how a wake meets one.
        await WritePreMigrationBackupAsync(host);

        host.RestartWithoutControlDatabase();

        // Restored, migrated, and stored again. Without the last step the store would still hold the
        // older shape, and every wake until someone happened to create a project would migrate afresh.
        var detail = await host.GetJsonAsync<ProjectDetail>("/api/projects/legacy");
        Assert.False(detail.SingleRepository);
        Assert.True(await StoredControlIsMigratedAsync(host));
    }

    [Fact]
    public async Task A_startup_that_migrates_nothing_leaves_the_backup_alone()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        var written = File.GetLastWriteTimeUtc(host.DurableControlBackup);

        host.RestartWithoutControlDatabase();

        // The ordinary wake: the restored file is already the shape this build reads, so the statements
        // change nothing and the store is not written again. Uploading on every wake would be the
        // easy way to be correct here, and it is the one this deliberately does not take.
        Assert.Equal(written, File.GetLastWriteTimeUtc(host.DurableControlBackup));
    }

    [Fact]
    public async Task Deleting_a_project_takes_its_durable_copy_with_it()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        Assert.True(Directory.Exists(host.DurableIndexDirectory("alpha")));

        using var http = host.CreateClient();
        using var response = await http.DeleteAsync("/api/projects/alpha", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // Left behind, it would restore a deleted project's files under a slug someone reused.
        Assert.False(Directory.Exists(host.DurableIndexDirectory("alpha")));
    }

    [Fact]
    public async Task A_project_delete_whose_backup_fails_still_removes_its_index_and_local_copies()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        var project = await host.Services.GetRequiredService<ControlDatabase>().FindAsync("alpha", Ct);
        Assert.NotNull(project);
        // A store that refuses the backup: the local store copies over this file, and cannot over a
        // read-only one.
        File.SetAttributes(host.DurableControlBackup, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<BackupFailedException>(() =>
                host.Services.GetRequiredService<ProjectOverview>().DeleteAsync(project, Ct));
        }
        finally
        {
            File.SetAttributes(host.DurableControlBackup, FileAttributes.Normal);
        }

        // The row went before the backup failed, so what hangs off it must go too: left behind, a project
        // created later under the slug would open this one's index and copies (GHSA-253f-grfp-cqq7).
        Assert.Null(await host.Services.GetRequiredService<ControlDatabase>().FindAsync("alpha", Ct));
        Assert.False(host.Indexes.HasIndex("alpha"));
        Assert.False(Directory.Exists(host.DurableIndexDirectory("alpha")));
        Assert.False(Directory.Exists(host.ClonePath("alpha", "one")));
    }

    /// <summary>
    ///     The same for one repository: its row goes before the backup fails, so its local copy must go
    ///     too, or it stays on disk with nothing that would ever lead to it again.
    /// </summary>
    [Fact]
    public async Task A_repository_removal_whose_backup_fails_still_removes_its_local_copy()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        var control = host.Services.GetRequiredService<ControlDatabase>();
        var project = await control.FindAsync("alpha", Ct);
        Assert.NotNull(project);
        Assert.True(Directory.Exists(host.ClonePath("alpha", "one")));
        File.SetAttributes(host.DurableControlBackup, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<BackupFailedException>(() =>
                host.Services.GetRequiredService<ProjectOverview>().DeleteRepositoryAsync(project, "one", Ct));
        }
        finally
        {
            File.SetAttributes(host.DurableControlBackup, FileAttributes.Normal);
        }

        Assert.Empty(await control.ListRepositoriesAsync("alpha", Ct));
        Assert.False(Directory.Exists(host.ClonePath("alpha", "one")));
    }

    [Fact]
    public async Task Deleting_a_project_that_was_never_indexed_is_not_an_error()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");

        using var http = host.CreateClient();
        using var response = await http.DeleteAsync("/api/projects/alpha", Ct);

        // There is no durable copy to forget, which is a state every project passes through.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_half_written_durable_copy_is_not_restored()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");
        // A store that stopped part-way. Every table is written by one store, so a missing one is
        // not a smaller index but an unknown one, and half of it is worse than none.
        File.Delete(Path.Combine(host.DurableIndexDirectory("alpha"), "lines.parquet"));

        Assert.Null(await host.Indexes.OpenAsync("alpha", Ct));
        Assert.False(host.Indexes.HasIndex("alpha"));
    }

    /// <summary>
    ///     Every table but <c>index_info</c>, whose absence is the subject of the tests above. Taken from
    ///     the schema's own list, so a table added to it is interrupted here too without a second edit.
    /// </summary>
    public static TheoryData<string> ContentTables => [.. ProjectIndexes.Tables.Where(table => table != "index_info")];

    /// <summary>
    ///     A re-store that stopped part-way (#187), at each table in turn and the history ones included.
    ///     The interruption is that table's durable file held open exclusively, so its write fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContentTables))]
    public async Task A_re_store_interrupted_at_any_table_reads_as_no_copy_and_one_refresh_repairs_it(string table)
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));

        await using (File.Open(Path.Combine(host.DurableIndexDirectory("alpha"), table + ".parquet"),
                         FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using (var response = await host.RequestRefreshAsync("alpha"))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await host.WaitForRefreshesAsync();
            Assert.Equal(RefreshState.Failed, (await host.RefreshStatusAsync("alpha")).State);
        }

        // The wake that follows a scale to zero, which is when a mixed copy would have been restored.
        host.DeleteIndexFile("alpha");
        Assert.Null(await host.Indexes.OpenAsync("alpha", Ct));
        Assert.False(host.Indexes.HasIndex("alpha"));

        // An ordinary refresh and nothing else: absent means rebuilt from git, and the store it ends
        // with writes the whole set again.
        await host.RefreshAsync("alpha");
        Assert.Equal(["class Alpha;"], await host.ScalarsAsync("alpha", "SELECT content FROM lines"));
        Assert.Equal(await IndexTableFilesAsync(host, "alpha"), StoredFileNames(host, "alpha"));
        host.DeleteIndexFile("alpha");
        Assert.Equal(["class Alpha;"], await host.ScalarsAsync("alpha", "SELECT content FROM lines"));
        // The history the mixed copy would have left stale, restored whole with the rest.
        Assert.Equal(["1 1 1"], await host.ScalarsAsync("alpha",
            "SELECT (SELECT count(*) FROM commits) || ' ' || (SELECT count(*) FROM commit_files) || ' ' "
            + "|| (SELECT count(DISTINCT path) FROM attribution)"));
    }

    [Fact]
    public async Task A_durable_copy_an_older_schema_wrote_is_rebuilt_rather_than_restored()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");
        await WriteOldIndexInfoAsync(host, "alpha");

        // Not an error and not a restore: the columns no longer fit the tables this build creates, so
        // the project reads as unindexed and a refresh rebuilds it from git. Refused on index_info
        // alone (#170): every other table is held open exclusively, so a fetch that reached any of
        // them would fail where one that stopped at the version answers absent.
        var held = Directory.EnumerateFiles(host.DurableIndexDirectory("alpha"))
            .Where(path => Path.GetFileName(path) != "index_info.parquet")
            .Select(path => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            .ToList();
        try
        {
            Assert.NotEmpty(held);
            Assert.Null(await host.Indexes.OpenAsync("alpha", Ct));
        }
        finally
        {
            foreach (var file in held) await file.DisposeAsync();
        }

        Assert.False(host.Indexes.HasIndex("alpha"));

        await host.RefreshAsync("alpha");
        Assert.Equal(["class Alpha;"], await host.ScalarsAsync("alpha", "SELECT content FROM lines"));
    }

    /// <summary>
    ///     The rule ADR-0004 states and CODING_STANDARDS repeats: absent configuration selects a folder,
    ///     so a plain <c>dotnet run</c> with an empty <c>appsettings</c> keeps a durable copy and needs
    ///     no Azure at all. Asserted on where the bytes land, because that is the half of the decision
    ///     that can be proven without an account — and the acceptance criterion is that no test needs one.
    /// </summary>
    [Fact]
    public async Task With_no_blob_account_configured_the_durable_copy_is_a_folder_under_the_data_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "local.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(local, "durable", Ct);
        var store = new DurableStore(Settings.Of(
            new Dictionary<string, string?> { ["Storage:DataDirectory"] = root }));

        await store.StoreAsync("indexes/alpha/files.parquet", local, Ct);

        Assert.True(File.Exists(Path.Combine(root, "durable", "indexes", "alpha", "files.parquet")));
        Assert.True(await store.FetchAsync("indexes/alpha/files.parquet", Path.Combine(root, "back.txt"), Ct));
        Assert.False(await store.FetchAsync("indexes/ghost/files.parquet", Path.Combine(root, "ghost.txt"), Ct));
        Directory.Delete(root, true);
    }

    /// <summary>
    ///     Replaces the stored control-database backup with one an older build wrote: a <c>projects</c>
    ///     table without <c>single_repository</c>. Built with DuckDB rather than hand-written, because
    ///     what it has to be is a database this build's own constructor can open and migrate.
    /// </summary>
    private static async Task WritePreMigrationBackupAsync(TestHost host)
    {
        string path = host.ScratchFile("legacy.duckdb");
        using (var connection = new DuckDBConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(Ct);
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  CREATE TABLE projects (slug VARCHAR PRIMARY KEY, name VARCHAR NOT NULL);
                                  INSERT INTO projects VALUES ('legacy', 'Legacy');
                                  CREATE TABLE repositories (
                                      project_slug VARCHAR NOT NULL,
                                      slug VARCHAR NOT NULL,
                                      url VARCHAR NOT NULL,
                                      credential VARCHAR,
                                      PRIMARY KEY (project_slug, slug));
                                  """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        File.Copy(path, host.DurableControlBackup, true);
    }

    /// <summary>
    ///     Whether the stored backup has the migrated column, read back out of the store the way a
    ///     later wake would read it.
    /// </summary>
    private static async Task<bool> StoredControlIsMigratedAsync(TestHost host)
    {
        string path = host.ScratchFile("stored.duckdb");
        File.Copy(host.DurableControlBackup, path, true);
        using var connection = new DuckDBConnection($"Data Source={path}");
        await connection.OpenAsync(Ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT count(*) FILTER (WHERE column_name = 'single_repository') = 1 AS migrated
                              FROM duckdb_columns() WHERE table_name = 'projects'
                              """;
        using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return reader.Flag("migrated");
    }

    /// <summary>
    ///     Rewrites <c>index_info.parquet</c> as a build of an older schema version wrote it. Through
    ///     DuckDB and a file of its own, because the version has to be readable by the same
    ///     <c>read_parquet</c> the restore uses — a hand-written file would prove only that garbage is
    ///     refused.
    /// </summary>
    private static async Task WriteOldIndexInfoAsync(TestHost host, string slug)
    {
        string path = Path.Combine(host.DurableIndexDirectory(slug), "index_info.parquet");
        using var connection =
            new DuckDBConnection($"Data Source={host.ScratchFile("fixture.duckdb")}");
        await connection.OpenAsync(Ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"COPY (SELECT 1 AS schema_version, now() AS built_at, false AS fts_indexed, false AS single_repository) "
            + $"TO '{path.Replace("'", "''")}' (FORMAT parquet)";
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static IEnumerable<string?> StoredFileNames(TestHost host, string slug) =>
        Directory.EnumerateFiles(host.DurableIndexDirectory(slug)).Select(Path.GetFileName).Order();

    /// <summary>
    ///     The Parquet file each table of the project's live index should be stored as, in the order
    ///     <see cref="StoredFileNames" /> lists them. The main schema only: the BM25 index lives in
    ///     <c>fts</c>'s own schema and is rebuilt on a restore, never stored.
    /// </summary>
    private static async Task<IEnumerable<string?>> IndexTableFilesAsync(TestHost host, string slug) =>
        (await host.ScalarsAsync(slug,
            "SELECT table_name FROM duckdb_tables() "
            + "WHERE database_name = current_database() AND schema_name = 'main'"))
        .Select(string? (table) => table + ".parquet").Order();

    /// <summary>One repository of one file. The slug names the fixture on disk too, so two projects need two.</summary>
    private static Dictionary<string, Dictionary<string, string>> Repository(string content, string slug = "one") =>
        new() { [slug] = new Dictionary<string, string> { ["src/A.cs"] = content } };

    private TestHost Start(SearchEngine engine) => _host = new TestHost(engine);
}
