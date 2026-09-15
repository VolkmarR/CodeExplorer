using System.Net;
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

    private TestHost Start(SearchEngine engine) => _host = new TestHost(engine);
}
