using System.Net;
using System.Net.Http.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
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

        // Spelled out rather than derived from the table list, so adding a table to the index without
        // adding it to the durable copy fails here instead of on the next scale to zero. The history
        // tables are as much of the index as the code ones are (ADR-0007), and that includes the
        // rename chains the build derives from them (#148): a restore does not re-walk.
        Assert.Equal([
                "attribution.parquet", "commit_files.parquet", "commits.parquet", "files.parquet",
                "imports.parquet", "index_info.parquet", "lines.parquet", "path_lineage.parquet",
                "project_overview.parquet", "repositories.parquet"
            ],
            Directory.EnumerateFiles(host.DurableIndexDirectory("alpha")).Select(Path.GetFileName).Order());
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
        var before = await DetailAsync(host, "alpha");
        host.DeleteIndexFile("alpha");

        var after = await DetailAsync(host, "alpha");

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

    [Fact]
    public async Task The_project_list_reads_every_project_without_restoring_any_of_them()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");
        host.Restart();

        using var http = host.CreateClient();
        var projects = await http.GetFromJsonAsync<List<ProjectSummary>>("/api/projects", Ct);

        // The page shows the project and reports it as not built, which is what the disk says. Drawing
        // one list must not wake every durable copy, so this is the read that deliberately does not.
        Assert.Null(Assert.Single(projects!).Index.BuiltAt);
        Assert.False(host.Indexes.HasIndex("alpha"));
    }

    [Fact]
    public async Task Deleting_the_control_database_and_restarting_restores_it_from_its_backup()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "one",
            host.CreateGitRepository("one", new Dictionary<string, string> { ["src/A.cs"] = "class A;\n" }));

        host.DeleteControlDatabase();
        host.Restart();

        using var http = host.CreateClient();
        var detail = await http.GetFromJsonAsync<ProjectDetail>("/api/projects/alpha", Ct);
        Assert.NotNull(detail);
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
        host.DeleteControlDatabase();

        host.Restart();

        // Restored, migrated, and stored again. Without the last step the store would still hold the
        // older shape, and every wake until someone happened to create a project would migrate afresh.
        using var http = host.CreateClient();
        var detail = await http.GetFromJsonAsync<ProjectDetail>("/api/projects/legacy", Ct);
        Assert.NotNull(detail);
        Assert.False(detail.SingleRepository);
        Assert.True(await StoredControlIsMigratedAsync(host));
    }

    [Fact]
    public async Task A_startup_that_migrates_nothing_leaves_the_backup_alone()
    {
        var host = Start(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");
        var written = File.GetLastWriteTimeUtc(host.DurableControlBackup);
        host.DeleteControlDatabase();

        host.Restart();

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

    private static async Task<ProjectDetail> DetailAsync(TestHost host, string slug)
    {
        using var http = host.CreateClient();
        var detail = await http.GetFromJsonAsync<ProjectDetail>($"/api/projects/{slug}", Ct);
        Assert.NotNull(detail);
        return detail;
    }

    /// <summary>One repository of one file. The slug names the fixture on disk too, so two projects need two.</summary>
    private static Dictionary<string, Dictionary<string, string>> Repository(string content, string slug = "one") =>
        new() { [slug] = new Dictionary<string, string> { ["src/A.cs"] = content } };

    private TestHost Start(SearchEngine engine) => _host = new TestHost(engine);
}
