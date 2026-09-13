using System.Net;
using System.Net.Http.Json;
using DuckDB.NET.Data;
using LibGit2Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A project is ingested from its bare clones into its own DuckDB file (ADR-0003). Every test
///     pins the search engine, because <c>INSTALL fts</c> fails silently offline and the two engines
///     produce different schemas; an unpinned suite would prove different things on different machines.
/// </summary>
public sealed class ProjectIndexTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program>? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        if (!Directory.Exists(_root)) return;
        // libgit2 marks pack files read-only; Delete(recursive) refuses those unless cleared first.
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Two_repositories_land_in_one_project_file_with_distinct_qualified_paths()
    {
        var indexes = Start(SearchEngine.Fts);
        string one = CreateGitRepository("one", new Dictionary<string, string>
        {
            ["src/index.ts"] = "export const one = 1;\nconsole.log(one);",
            ["README.md"] = "first"
        });
        string two = CreateGitRepository("two", new Dictionary<string, string>
        {
            ["src/index.ts"] = "export const two = 2;",
            ["logo.png"] = "PNG\0\0binary"
        });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "one", one);
        await AddRepositoryAsync("alpha", "two", two);

        var summary = await IndexAsync("alpha");

        Assert.Equal(2, summary.Repositories);
        Assert.Equal(4, summary.Files);
        Assert.Equal(4, summary.Lines);
        Assert.True(File.Exists(Path.Combine(_root, "data", "indexes", "alpha.duckdb")));

        using var connection = await OpenAsync(indexes, "alpha");
        var qualified = await ScalarsAsync(connection, "SELECT qualified_path FROM files ORDER BY qualified_path");
        Assert.Equal(["one/README.md", "one/src/index.ts", "two/logo.png", "two/src/index.ts"], qualified);

        // A committed binary is still a file the tree knows about, only its lines are absent.
        var skipped = await ScalarsAsync(connection, "SELECT skip_reason FROM files WHERE skip_reason IS NOT NULL");
        Assert.Equal(["binary"], skipped);

        // Paths stay repository-relative and repo_id scopes them, so the same path exists twice.
        var relative = await ScalarsAsync(connection, """
            SELECT f.path FROM files f JOIN repositories r USING (repo_id)
            WHERE r.slug = 'two' AND f.skip_reason IS NULL
            """);
        Assert.Equal(["src/index.ts"], relative);

        var second = await ScalarsAsync(connection,
            "SELECT content FROM lines l JOIN files f USING (file_id) WHERE f.qualified_path = 'one/src/index.ts' AND l.line_number = 2");
        Assert.Equal(["console.log(one);"], second);
    }

    [Fact]
    public async Task Full_text_index_answers_after_use()
    {
        var indexes = Start(SearchEngine.Fts);
        string source = CreateGitRepository("source", new Dictionary<string, string>
        {
            ["a.cs"] = "class Alpha {}\nvoid Needle() {}",
            ["b.cs"] = "class Beta {}"
        });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "main", source);
        await IndexAsync("alpha");

        using var connection = await OpenAsync(indexes, "alpha");
        var matches = await ScalarsAsync(connection, """
            SELECT content FROM lines
            WHERE fts_main_lines.match_bm25(line_id, 'needle') IS NOT NULL
            """);
        Assert.Equal(["void Needle() {}"], matches);
    }

    [Fact]
    public async Task Concurrent_connections_to_different_projects_do_not_interfere()
    {
        var indexes = Start(SearchEngine.Fts);
        string a = CreateGitRepository("a", new Dictionary<string, string> { ["only-in-alpha.txt"] = "alpha\nalpha" });
        string b = CreateGitRepository("b", new Dictionary<string, string> { ["only-in-beta.txt"] = "beta" });
        await CreateProjectAsync("alpha");
        await CreateProjectAsync("beta");
        await AddRepositoryAsync("alpha", "main", a);
        await AddRepositoryAsync("beta", "main", b);

        // Both builds run at once: two files, two ATTACHes, one instance.
        await Task.WhenAll(IndexAsync("alpha"), IndexAsync("beta"));

        // Several threads, each with its own connection, query both projects at the same time. A
        // connection is not thread-safe, so every thread checks out its own; the instance is shared.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            string slug = i % 2 == 0 ? "alpha" : "beta";
            using var connection = await OpenAsync(indexes, slug);
            var paths = new List<string>();
            for (int round = 0; round < 5; round++)
                paths.AddRange(await ScalarsAsync(connection, "SELECT qualified_path FROM files"));
            return (slug, paths);
        })));

        foreach (var (slug, paths) in results)
            Assert.Equal(Enumerable.Repeat($"main/only-in-{slug}.txt", 5), paths);
    }

    [Fact]
    public async Task A_project_without_an_index_opens_as_null()
    {
        var indexes = Start(SearchEngine.Substring);
        await CreateProjectAsync("alpha");

        Assert.Null(await indexes.OpenAsync("alpha", Ct));
        Assert.False(indexes.HasIndex("alpha"));
    }

    [Fact]
    public async Task Substring_engine_builds_no_full_text_index_and_still_answers()
    {
        var indexes = Start(SearchEngine.Substring);
        string source = CreateGitRepository("source", new Dictionary<string, string> { ["a.cs"] = "void Needle() {}" });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "main", source);
        await IndexAsync("alpha");

        using var connection = await OpenAsync(indexes, "alpha");
        var schemas = await ScalarsAsync(connection,
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'fts_main_lines'");
        Assert.Empty(schemas);
        var matches = await ScalarsAsync(connection, "SELECT content FROM lines WHERE content LIKE '%Needle%'");
        Assert.Equal(["void Needle() {}"], matches);
    }

    [Fact]
    public async Task Indexing_an_unknown_project_is_404_and_a_project_without_repositories_is_empty()
    {
        Start(SearchEngine.Substring);
        using var http = _factory!.CreateClient();

        using var missing = await http.PostAsync("/api/projects/nope/index", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await CreateProjectAsync("alpha");
        var summary = await IndexAsync("alpha");
        Assert.Equal(0, summary.Repositories);
        Assert.Equal(0, summary.Files);
    }

    [Fact]
    public async Task Lfs_repository_is_skipped_with_its_reason_in_the_summary()
    {
        Start(SearchEngine.Substring);
        string lfs = CreateGitRepository("lfs", new Dictionary<string, string>
        {
            [".gitattributes"] = "*.bin filter=lfs diff=lfs merge=lfs -text\n",
            ["model.bin"] = "version https://git-lfs.github.com/spec/v1"
        });
        string plain = CreateGitRepository("plain", new Dictionary<string, string> { ["a.txt"] = "a" });
        await CreateProjectAsync("alpha");
        await AddRepositoryAsync("alpha", "big", lfs);
        await AddRepositoryAsync("alpha", "small", plain);

        var summary = await IndexAsync("alpha");

        Assert.Equal(1, summary.Repositories);
        Assert.Equal(1, summary.Files);
        string skipped = Assert.Single(summary.Skipped);
        Assert.Contains("big", skipped);
        Assert.Contains("LFS", skipped);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectIndexes Start(SearchEngine engine)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:DataDirectory", Path.Combine(_root, "data"));
            builder.UseSetting("Index:SearchEngine", engine.ToString());
        });
        return _factory.Services.GetRequiredService<ProjectIndexes>();
    }

    /// <summary>Builds a non-bare repository with one commit holding the given files.</summary>
    private string CreateGitRepository(string name, Dictionary<string, string> files)
    {
        string path = Path.Combine(_root, "fixtures", name);
        Repository.Init(path);
        using var repo = new Repository(path);
        foreach (var (relative, content) in files)
        {
            string full = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Commands.Stage(repo, relative);
        }

        var author = new Signature("Test", "test@example.invalid", DateTimeOffset.UnixEpoch);
        repo.Commit("fixture", author, author);
        return path;
    }

    private async Task CreateProjectAsync(string slug)
    {
        using var http = _factory!.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/projects", new { slug, name = slug }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task AddRepositoryAsync(string project, string slug, string url)
    {
        using var http = _factory!.CreateClient();
        using var response = await http.PostAsJsonAsync($"/api/projects/{project}/repositories", new { slug, url }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<IndexSummary> IndexAsync(string project)
    {
        using var http = _factory!.CreateClient();
        using var response = await http.PostAsync($"/api/projects/{project}/index", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<IndexSummary>(Ct);
        Assert.NotNull(summary);
        return summary;
    }

    private static async Task<DuckDBConnection> OpenAsync(ProjectIndexes indexes, string slug)
    {
        var connection = await indexes.OpenAsync(slug, Ct);
        Assert.NotNull(connection);
        return connection;
    }

    private static async Task<List<string>> ScalarsAsync(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct)) values.Add(reader.GetString(0));
        return values;
    }
}
