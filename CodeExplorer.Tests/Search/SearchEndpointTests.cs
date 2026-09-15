using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The JSON browse, search and file reads the web UI renders. The engine is pinned in every test
///     and both paths are covered, because <c>INSTALL fts</c> fails offline and an unpinned suite
///     would test full-text on a laptop and a substring scan on CI, which rank differently.
/// </summary>
public sealed class SearchEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A project of two repositories that each hold a <c>src/Widget.cs</c>, plus a doc file.</summary>
    private static async Task<TestHost> ProjectAsync(SearchEngine engine)
    {
        var host = new TestHost(engine);
        try
        {
            await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
            {
                ["one"] = new()
                {
                    ["docs/Widget.md"] = "widget notes\n",
                    ["src/Widget.cs"] = "class Widget\n{\n    int Size;\n}\n"
                },
                ["two"] = new() { ["src/Widget.cs"] = "class Widget { }\n" }
            });
            return host;
        }
        catch
        {
            // The host owns a data directory and an open DuckDB instance; a failure here would leak both.
            host.Dispose();
            throw;
        }
    }

    [Theory]
    [InlineData(SearchEngine.Substring)]
    [InlineData(SearchEngine.Fts)]
    public async Task Search_answers_with_qualified_paths_the_file_endpoint_accepts(SearchEngine engine)
    {
        using var host = await ProjectAsync(engine);

        var result = await GetAsync<GrepResult>(host, "/api/projects/alpha/search?q=Widget");

        Assert.Equal(3, result.TotalFiles);
        // Two repositories each hold src/Widget.cs; only the qualified path tells them apart, which is
        // exactly what the result list must link through with.
        Assert.Contains(result.Files, f => f.QualifiedPath == "one/src/Widget.cs");
        Assert.Contains(result.Files, f => f.QualifiedPath == "two/src/Widget.cs");

        var file = await GetAsync<FileContentResponse>(host,
            "/api/projects/alpha/file?path=" + Uri.EscapeDataString("one/src/Widget.cs"));
        Assert.Equal("one", file.RepositorySlug);
        Assert.Equal(4, file.LineCount);
        Assert.Equal("class Widget\n{\n    int Size;\n}", file.Content);
        Assert.Null(file.SkipReason);
    }

    [Theory]
    [InlineData(SearchEngine.Substring)]
    [InlineData(SearchEngine.Fts)]
    public async Task Search_filters_by_extension_and_pages(SearchEngine engine)
    {
        using var host = await ProjectAsync(engine);

        var scoped = await GetAsync<GrepResult>(host, "/api/projects/alpha/search?q=Widget&extension=cs");
        Assert.Equal(2, scoped.TotalFiles);
        Assert.DoesNotContain(scoped.Files, f => f.QualifiedPath.EndsWith(".md", StringComparison.Ordinal));

        var page = await GetAsync<GrepResult>(host, "/api/projects/alpha/search?q=Widget&pageSize=1&page=2");
        Assert.Equal(3, page.TotalFiles);
        Assert.Single(page.Files);
    }

    [Theory]
    [InlineData(SearchEngine.Substring)]
    [InlineData(SearchEngine.Fts)]
    public async Task Browsing_lists_files_by_glob_and_by_repository(SearchEngine engine)
    {
        using var host = await ProjectAsync(engine);

        var all = await GetAsync<FileListResponse>(host, "/api/projects/alpha/files");
        Assert.Equal(3, all.Total);

        var sources = await GetAsync<FileListResponse>(host, "/api/projects/alpha/files?glob=*.cs");
        Assert.Equal(2, sources.Total);
        Assert.All(sources.Files, f => Assert.EndsWith(".cs", f.QualifiedPath, StringComparison.Ordinal));

        // Both repositories hold src/Widget.cs, so scoping is the only thing that separates them.
        var scoped = await GetAsync<FileListResponse>(host, "/api/projects/alpha/files?repository=two");
        Assert.Equal("two/src/Widget.cs", Assert.Single(scoped.Files).QualifiedPath);
    }

    [Theory]
    [InlineData(SearchEngine.Substring, 1)]
    [InlineData(SearchEngine.Fts, 0)]
    public async Task The_engine_the_result_names_is_the_one_that_answered(SearchEngine engine, int expected)
    {
        using var host = new TestHost(engine);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
            { ["one"] = new() { ["src/A.cs"] = "class WidgetFactory { }\n" } });

        var result = await GetAsync<GrepResult>(host, "/api/projects/alpha/search?q=Widget");

        // The one place the two engines legitimately disagree, and why the result carries the engine:
        // full-text matches whole identifier tokens, so `WidgetFactory` is not a hit for `Widget`,
        // while a substring scan finds it. Neither is wrong; the UI shows which one ran.
        Assert.Equal(expected, result.TotalFiles);
        Assert.Equal(engine == SearchEngine.Fts ? GrepSearch.FullTextEngine : GrepSearch.SubstringEngine,
            result.Engine);
    }

    [Fact]
    public async Task A_pattern_RE2_refuses_is_an_explanation_not_an_empty_result()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        using var http = host.CreateClient();
        using var response = await http.GetAsync("/api/projects/alpha/search?q=(?%3D%3Dfoo)&regex=true", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lookahead", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Searching_or_browsing_a_project_with_no_index_says_that_rather_than_returning_nothing()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("alpha");

        using var http = host.CreateClient();
        using var search = await http.GetAsync("/api/projects/alpha/search?q=Widget", Ct);
        using var files = await http.GetAsync("/api/projects/alpha/files", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, search.StatusCode);
        Assert.Contains("alpha", await search.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, files.StatusCode);
        Assert.Contains("alpha", await files.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A project that does not exist is told apart from one that has no index yet: the route binds
        // the project first (BoundProject), so every route under it gives the one sentence, and the
        // search route does not get to say 400 about an index that has no project to belong to.
        foreach (string route in new[] { "search?q=Widget", "files", "tree", "file?path=x" })
        {
            using var response = await http.GetAsync($"/api/projects/ghost/{route}", Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("No project with slug 'ghost'", await response.Content.ReadAsStringAsync(Ct),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Browsing_an_unknown_repository_is_an_explanation_not_an_empty_listing()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        // A repository slug that names nothing is the one browse argument that could pass for a clean
        // negative: an empty list looks exactly like "nothing matched". Both routes that take one say
        // what exists instead, as a 400 because the project is there and the request named something
        // in it that is not.
        using var http = host.CreateClient();
        using var files = await http.GetAsync("/api/projects/alpha/files?repository=three", Ct);
        using var tree = await http.GetAsync("/api/projects/alpha/tree?path=three/src", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, files.StatusCode);
        string explanation = await files.Content.ReadAsStringAsync(Ct);
        Assert.Contains("No repository 'three' in project 'alpha'", explanation, StringComparison.Ordinal);
        Assert.Contains("one, two", explanation, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, tree.StatusCode);
        Assert.Contains("No repository 'three'", await tree.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Case is forgiven the way it is for a path, and the answer is spelled the way the index holds it.
        var scoped = await GetAsync<FileListResponse>(host, "/api/projects/alpha/files?repository=TWO");
        Assert.Equal("two", Assert.Single(scoped.Files).RepositorySlug);
    }

    [Fact]
    public async Task An_unknown_file_is_a_not_found()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        using var http = host.CreateClient();
        using var response = await http.GetAsync("/api/projects/alpha/file?path=one/src/Missing.cs", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_tree_walks_repositories_then_directories_then_files()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        // The root of a project is its repositories: a qualified path begins with one, so there is no
        // level above them to list.
        var root = await GetAsync<TreeResponse>(host, "/api/projects/alpha/tree");
        Assert.Equal("", root.Path);
        Assert.Equal(["one", "two"], root.Entries.Select(e => e.Name));
        Assert.Equal(2, root.Entries[0].Files);
        Assert.Equal(1, root.Entries[1].Files);

        // Inside a repository: directories only here, and each counts what lies beneath it rather than
        // its immediate children.
        var repository = await GetAsync<TreeResponse>(host, "/api/projects/alpha/tree?path=one");
        Assert.Equal("one", repository.Path);
        Assert.Equal(["docs", "src"], repository.Entries.Select(e => e.Name));
        Assert.All(repository.Entries, e => Assert.NotNull(e.Files));
        Assert.Equal("one/docs", repository.Entries[0].QualifiedPath);

        var source = await GetAsync<TreeResponse>(host, "/api/projects/alpha/tree?path=one/src");
        var file = Assert.Single(source.Entries);
        Assert.Equal("Widget.cs", file.Name);
        // Null `Files` is what tells a file from a directory, and the qualified path is what the file
        // view is opened with.
        Assert.Null(file.Files);
        Assert.Equal("one/src/Widget.cs", file.QualifiedPath);
        Assert.Equal(4, file.Lines);
    }

    [Fact]
    public async Task A_repository_root_lists_its_own_files_beside_its_directories()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["README.md"] = "read me\n", ["src/A.cs"] = "class A { }\n" }
        });

        var level = await GetAsync<TreeResponse>(host, "/api/projects/alpha/tree?path=one");

        // A file at the repository root has an empty `directory`, which is also the prefix this level
        // matches on. The listing must show it as a file and must not turn it into a directory.
        Assert.Equal(["src", "README.md"], level.Entries.Select(e => e.Name));
        Assert.NotNull(level.Entries[0].Files);
        Assert.Null(level.Entries[1].Files);
    }

    [Fact]
    public async Task A_directory_that_is_not_in_the_index_is_an_empty_level_not_a_not_found()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        // Only a project with no index at all is a 404 here. An empty level is an answer: the view
        // still draws the breadcrumb that leads back out of it.
        var level = await GetAsync<TreeResponse>(host, "/api/projects/alpha/tree?path=one/nowhere");

        Assert.Empty(level.Entries);
    }

    [Fact]
    public async Task A_single_repository_project_names_files_without_a_slug()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("solo", new Dictionary<string, Dictionary<string, string>>
            { ["only"] = new() { ["README.md"] = "read me\n", ["src/Widget.cs"] = "class Widget { }\n" } },
            true);

        // The stored qualified path is the short one, so a listing, a search and a file read all agree
        // without anyone rewriting anything (ADR-0006).
        var files = await GetAsync<FileListResponse>(host, "/api/projects/solo/files?glob=*");
        Assert.Equal(["README.md", "src/Widget.cs"], files.Files.Select(f => f.QualifiedPath).Order());

        var file = await GetAsync<FileContentResponse>(host,
            "/api/projects/solo/file?path=" + Uri.EscapeDataString("src/Widget.cs"));
        Assert.Equal("src/Widget.cs", file.QualifiedPath);

        var search = await GetAsync<GrepResult>(host, "/api/projects/solo/search?q=Widget");
        Assert.Equal("src/Widget.cs", Assert.Single(search.Files).QualifiedPath);
    }

    [Fact]
    public async Task The_tree_of_a_single_repository_project_opens_inside_it()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("solo", new Dictionary<string, Dictionary<string, string>>
            { ["only"] = new() { ["README.md"] = "read me\n", ["src/Widget.cs"] = "class Widget { }\n" } },
            true);

        // There is no repository level to walk through: the root is the repository's own top level, and
        // the entries under it are named without a slug.
        var root = await GetAsync<TreeResponse>(host, "/api/projects/solo/tree");
        Assert.Equal(["src", "README.md"], root.Entries.Select(e => e.Name));
        Assert.Equal("src", root.Entries[0].QualifiedPath);
        Assert.Equal("README.md", root.Entries[1].QualifiedPath);

        var source = await GetAsync<TreeResponse>(host, "/api/projects/solo/tree?path=src");
        Assert.Equal("src/Widget.cs", Assert.Single(source.Entries).QualifiedPath);
    }

    private static async Task<T> GetAsync<T>(TestHost host, string url)
    {
        using var http = host.CreateClient();
        var value = await http.GetFromJsonAsync<T>(url, Ct);
        Assert.NotNull(value);
        return value;
    }
}
