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

        // Both routes answer the missing index the same way: the view renders a 404 as the starting
        // state a new project is in, and a search page is no less in that state than a browse page.
        Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
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
    public async Task A_directory_that_is_not_in_the_index_is_explained_not_shown_as_an_empty_level()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        // The same sentence list_tree gives an agent, as a 400 the view draws as "nothing here": an
        // empty level would read as a directory that exists and holds nothing, which is a different fact.
        using var http = host.CreateClient();
        using var response = await http.GetAsync("/api/projects/alpha/tree?path=one/nowhere", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'nowhere' is not a directory in repository 'one'",
            await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
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

    /// <summary>
    ///     The change log the history page draws: paged over the project, scoped to one repository, and
    ///     each commit's files reachable with the qualified path the file view opens — which is the whole
    ///     point of listing them, and the one thing a path alone could not give a two-repository project.
    /// </summary>
    [Fact]
    public async Task The_change_log_pages_the_commits_and_links_the_files_each_one_touched()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        // One commit per fixture repository. A page of one still says there are two.
        var page = await GetAsync<CommitListResponse>(host, "/api/projects/alpha/commits?pageSize=1");
        Assert.Equal(2, page.Total);
        Assert.Single(page.Commits);

        var scoped = await GetAsync<CommitListResponse>(host, "/api/projects/alpha/commits?repository=one");
        Assert.Equal(1, scoped.Total);
        var commit = Assert.Single(scoped.Commits);
        Assert.Equal("one", commit.RepositorySlug);
        Assert.Equal(2, commit.FilesChanged);
        // The root commit adds every line of both files: 1 in the doc, 4 in the source.
        Assert.Equal(5, commit.Added);
        Assert.Equal(0, commit.Deleted);

        var files = await GetAsync<CommitFilesResponse>(host, $"/api/projects/alpha/commits/{commit.Sha}/files");
        Assert.Equal(["one/docs/Widget.md", "one/src/Widget.cs"], files.Files.Select(f => f.QualifiedPath));
        Assert.All(files.Files, f => Assert.Equal("added", f.ChangeKind));

        using var http = host.CreateClient();
        using var missing = await http.GetAsync("/api/projects/alpha/commits/0000000000000000000000000000000000000000/files", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    ///     The churn page. It ranks over a window of days rather than a page of commits, and answers
    ///     with the dates that window resolved to — which end at the newest recorded commit and not at
    ///     today, so a caller can see that an index is stale rather than read an empty ranking as
    ///     "nothing changed" (CONTEXT.md, Window).
    /// </summary>
    [Fact]
    public async Task The_churn_page_ranks_over_a_window_of_days_and_says_which_dates_it_covered()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        var all = await GetAsync<ChurnResponse>(host, "/api/projects/alpha/churn");
        Assert.NotNull(all.Since);
        // The fixture's commits are at the Unix epoch, decades before today: a window measured from
        // the clock would rank nothing at all, and this is the assertion that says which it is.
        Assert.Equal(DateTimeOffset.UnixEpoch, all.Until);
        // Both root commits, so all three files of the fixture are ranked and each is still at HEAD.
        Assert.Equal(["one/docs/Widget.md", "one/src/Widget.cs", "two/src/Widget.cs"],
            all.Files.Select(f => f.QualifiedPath).Order());
        Assert.All(all.Files, f => Assert.Equal(1, f.Commits));
        Assert.All(all.Files, f => Assert.True(f.AtHead));

        // Scoped to one repository, the ranking covers only that repository.
        var scoped = await GetAsync<ChurnResponse>(host, "/api/projects/alpha/churn?repository=two");
        Assert.Equal("two/src/Widget.cs", Assert.Single(scoped.Files).QualifiedPath);

        // A one-day window still reaches the newest commit, because it ends there rather than today.
        var narrow = await GetAsync<ChurnResponse>(host, "/api/projects/alpha/churn?days=1");
        Assert.NotEmpty(narrow.Files);
        Assert.Equal(all.Until, narrow.Until);

        // Both fixture repositories were walked, so there is nothing to caveat. A repository the walk
        // found nothing in has to be named, or half a project's churn reads as all of it.
        Assert.Empty(all.WithoutHistory);
        await host.ExecuteAsync("alpha", "DELETE FROM commits WHERE repo_slug = 'two'");
        var partial = await GetAsync<ChurnResponse>(host, "/api/projects/alpha/churn");
        Assert.Equal("two", Assert.Single(partial.WithoutHistory));
        Assert.DoesNotContain(partial.Files, f => f.RepositorySlug == "two");
    }

    /// <summary>
    ///     A project whose history was never imported answers with no dates and no ranking, rather
    ///     than a failure: the page draws that as its own starting state.
    /// </summary>
    [Fact]
    public async Task The_churn_page_of_a_project_without_history_answers_with_no_window()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);
        await host.ExecuteAsync("alpha", "DELETE FROM commits");

        var churn = await GetAsync<ChurnResponse>(host, "/api/projects/alpha/churn");

        Assert.Null(churn.Since);
        Assert.Null(churn.Until);
        Assert.Empty(churn.Files);
    }

    /// <summary>
    ///     A project whose import edges cover the three answers the rail has to keep apart: a name
    ///     that resolved, a name that could not be, and a file whose language has no imports at all.
    /// </summary>
    private static async Task<TestHost> ImportingProjectAsync()
    {
        var host = new TestHost(SearchEngine.Substring);
        try
        {
            await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
            {
                ["one"] = new()
                {
                    // Two files declare Orders.Storage, so an import of it names neither.
                    ["src/Orders.cs"] =
                        "namespace Orders.Domain;\n\nusing System.Text;\nusing Orders.Storage;\n\npublic class OrderService;\n",
                    ["src/Report.cs"] = "namespace Orders.Reports;\n\nusing Orders.Domain;\n\npublic class Report;\n",
                    ["src/Storage.cs"] = "namespace Orders.Storage;\n\npublic class Store;\n",
                    ["src/Storage.Extra.cs"] = "namespace Orders.Storage;\n\npublic class Extra;\n",
                    ["db/install.sql"] = "create table orders (id integer);\n",
                    ["build/notes.rst"] = ".. include:: other.rst\n"
                }
            });
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     The two directions the file page draws beside the code. Resolved and unresolved edges come
    ///     back in one list, each carrying what it turned out to be, because the panel shows both and
    ///     an edge dropped for not resolving would read as a dependency the file does not have.
    /// </summary>
    /// <summary>
    ///     The blame gutter. The runs are what the gutter draws, so they must cover the file and name
    ///     the commit the change log lists for it — the two pages are read together and must agree.
    /// </summary>
    [Fact]
    public async Task The_blame_page_covers_the_file_with_runs_naming_the_commit_the_change_log_lists()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        var log = await GetAsync<CommitListResponse>(host, "/api/projects/alpha/commits?repository=one");
        var commit = Assert.Single(log.Commits);

        var blame = await GetAsync<BlameResponse>(host,
            "/api/projects/alpha/file/blame?path=" + Uri.EscapeDataString("one/src/Widget.cs"));

        Assert.Equal("one/src/Widget.cs", blame.QualifiedPath);
        // One root commit wrote every line, so the whole file is one run.
        var run = Assert.Single(blame.Runs);
        Assert.Equal(1, run.StartLine);
        Assert.Equal(4, run.EndLine);
        Assert.NotNull(run.By);
        Assert.Equal(commit.Sha, run.By.Sha);
        Assert.Equal(commit.AuthorName, run.By.AuthorName);
    }

    [Fact]
    public async Task The_blame_page_of_a_file_without_lines_has_no_runs_to_draw()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            // A NUL byte makes libgit2 classify the blob as binary, so the index keeps the row without lines.
            ["one"] = new() { ["assets/logo.bin"] = "\0\0binary", ["src/A.cs"] = "class A { }\n" }
        });

        var blame = await GetAsync<BlameResponse>(host,
            "/api/projects/alpha/file/blame?path=" + Uri.EscapeDataString("one/assets/logo.bin"));

        // The file is in the index and its page renders; only the gutter has nothing to fill.
        Assert.Equal("one/assets/logo.bin", blame.QualifiedPath);
        Assert.Empty(blame.Runs);
    }

    [Fact]
    public async Task The_blame_page_of_an_unknown_file_is_a_not_found()
    {
        using var host = await ProjectAsync(SearchEngine.Substring);

        using var http = host.CreateClient();
        using var response = await http.GetAsync("/api/projects/alpha/file/blame?path=one/src/Missing.cs", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("one/src/Missing.cs", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_page_reads_both_directions_of_the_import_graph()
    {
        using var host = await ImportingProjectAsync();

        var imports = await GetAsync<FileImportsResponse>(host, Route("imports", "one/src/Orders.cs"));

        Assert.True(imports.Profiled);
        Assert.True(imports.HasImports);
        Assert.Equal("C#", imports.LanguageName);
        Assert.Equal("Orders.Domain", imports.Module);
        Assert.False(imports.Capped);
        // Nothing here is System.Text, and Orders.Storage is two files: neither resolves, and both
        // are in the answer as the names they were.
        Assert.Equal(["Orders.Storage", "System.Text"], imports.Imports.Select(i => i.Name).Order());
        Assert.All(imports.Imports, i => Assert.Null(i.TargetPath));
        Assert.All(imports.Imports, i => Assert.NotNull(i.Unresolved));

        var resolved = await GetAsync<FileImportsResponse>(host, Route("imports", "one/src/Report.cs"));
        var edge = Assert.Single(resolved.Imports);
        // The link the panel draws: a qualified path the file route accepts, not the name as written.
        Assert.Equal("one/src/Orders.cs", edge.TargetPath);
        Assert.Equal("Orders.Domain", edge.Name);
        Assert.Equal(3, edge.LineNumber);

        var dependents = await GetAsync<FileDependentsResponse>(host, Route("dependents", "one/src/Orders.cs"));
        var dependent = Assert.Single(dependents.Dependents);
        Assert.Equal("one/src/Report.cs", dependent.QualifiedPath);
        Assert.Equal(3, dependent.LineNumber);
        Assert.Equal(0, dependents.ShareTheModule);
        Assert.False(dependents.Capped);
    }

    /// <summary>
    ///     The three ways an empty panel can mean something other than "nothing here", each answered
    ///     with the field that says which it is. A panel that drew them the same would tell a reader
    ///     a file depends on nothing when the truth is that nothing read it.
    /// </summary>
    [Fact]
    public async Task An_empty_answer_says_which_kind_of_empty_it_is()
    {
        using var host = await ImportingProjectAsync();

        // A language with imports, and a file that writes none.
        var none = await GetAsync<FileImportsResponse>(host, Route("imports", "one/src/Storage.cs"));
        Assert.True(none.Profiled);
        Assert.True(none.HasImports);
        Assert.Empty(none.Imports);

        // A language with no import concept at all.
        var sql = await GetAsync<FileImportsResponse>(host, Route("imports", "one/db/install.sql"));
        Assert.True(sql.Profiled);
        Assert.False(sql.HasImports);
        Assert.Equal("SQL", sql.LanguageName);

        // An extension no profile covers: its import lines were never read.
        var uncovered = await GetAsync<FileImportsResponse>(host, Route("imports", "one/build/notes.rst"));
        Assert.False(uncovered.Profiled);
        Assert.Empty(uncovered.Imports);

        // And the reverse direction's own kind of empty: a namespace two files declare resolves to
        // neither, so no edge could ever have pointed here.
        var shared = await GetAsync<FileDependentsResponse>(host, Route("dependents", "one/src/Storage.cs"));
        Assert.Empty(shared.Dependents);
        Assert.Equal("Orders.Storage", shared.Module);
        Assert.Equal(1, shared.ShareTheModule);
    }

    /// <summary>
    ///     The boundary the panel prints as "500+": a file with exactly as many imports as the ceiling
    ///     holds is complete, and one with a single import more is not. The two are a row apart and the
    ///     answers are opposite, which is why the read asks for one row past what it reports.
    /// </summary>
    [Fact]
    public async Task A_list_that_fills_the_ceiling_is_told_apart_from_one_the_ceiling_cut_short()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Exactly.cs"] = Usings(ImportGraph.MaxEdges),
                ["src/OneMore.cs"] = Usings(ImportGraph.MaxEdges + 1)
            }
        });

        var exactly = await GetAsync<FileImportsResponse>(host, Route("imports", "one/src/Exactly.cs"));
        Assert.Equal(ImportGraph.MaxEdges, exactly.Imports.Count);
        Assert.False(exactly.Capped);

        var more = await GetAsync<FileImportsResponse>(host, Route("imports", "one/src/OneMore.cs"));
        // Still only the ceiling is reported, and now the reply says the list is short of the answer.
        Assert.Equal(ImportGraph.MaxEdges, more.Imports.Count);
        Assert.True(more.Capped);
    }

    /// <summary>A C# file importing <paramref name="count" /> distinct names none of which resolve.</summary>
    private static string Usings(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"using Outside.Package{i};\n"));

    [Fact]
    public async Task A_path_that_names_no_file_is_explained_rather_than_answered_with_an_empty_panel()
    {
        using var host = await ImportingProjectAsync();

        using var http = host.CreateClient();
        using var response = await http.GetAsync(Route("imports", "one/src/Nowhere.cs"), Ct);

        // A 404 like the file page itself gives for the path: the panel is a page that is not there,
        // and the sentence is the one every reader gives a path that names no file.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No indexed file 'one/src/Nowhere.cs'", await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    private static string Route(string direction, string path) =>
        $"/api/projects/alpha/file/{direction}?path={Uri.EscapeDataString(path)}";

    /// <summary>
    ///     A project covering the three answers the declarations panel has to keep apart: a language
    ///     whose declarations are read, one a profile covers that declares nothing this can read, and
    ///     an extension no profile covers at all.
    /// </summary>
    private static async Task<TestHost> DeclaringProjectAsync()
    {
        var host = new TestHost(SearchEngine.Substring);
        try
        {
            await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
            {
                ["one"] = new()
                {
                    ["src/Orders.cs"] = """
                                        namespace Orders.Domain;

                                        public class OrderService
                                        {
                                            public int Count;
                                            // public void Removed() { }
                                            public void Place() { }
                                            public void Cancel() { }
                                        }

                                        """,
                    // A C# file that declares nothing: the language reads declarations, this file has none.
                    ["src/Empty.cs"] = "// Intentionally holds no declaration.\n",
                    // CSS is profiled and declares nothing this can read.
                    ["web/site.css"] = ".panel { color: red; }\n",
                    ["build/notes.rst"] = "nothing here\n"
                }
            });
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     What the file page's Declarations panel draws. The type and its members come back with the
    ///     line each sits on, because the panel links them; a declaration shape inside a comment does
    ///     not, because the index answers what the file declares and not what it once declared.
    /// </summary>
    [Fact]
    public async Task The_file_page_reads_what_a_file_declares()
    {
        using var host = await DeclaringProjectAsync();

        var declared = await GetAsync<FileDeclarationsResponse>(host,
            Route("declarations", "one/src/Orders.cs"));

        Assert.Equal("one/src/Orders.cs", declared.QualifiedPath);
        Assert.Equal("C#", declared.LanguageName);
        Assert.Equal("read", declared.Coverage);
        Assert.False(declared.Capped);

        // The type and its routines, in the order the file writes them. The field on line 5 is not
        // among them: the C-family member shape requires the name to be followed by `(`, `<`, `{` or
        // `=`, so an initialised field is a declaration and a bare one is not — #71 asks which way
        // the two should agree. A form no profile knows is one this does not find rather than one
        // that is not there (CONTEXT.md, Declaration).
        // Line 6 is the one that matters most: `// public void Removed() { }` is shaped exactly like
        // the live declaration two lines below it, and only the walk of the lines above tells them
        // apart.
        Assert.Equal([(3, "OrderService"), (7, "Place"), (8, "Cancel")],
            declared.Declarations.Select(d => (d.LineNumber, d.Type ?? d.Member)));
        // Read from line shape and not from a compiler, which is what the panel says beside the list.
        Assert.All(declared.Declarations, d => Assert.Equal("text", d.Evidence));
        // C# announces a routine where it writes it, so there is no side of a split to report.
        Assert.All(declared.Declarations, d => Assert.Null(d.Role));
    }

    /// <summary>
    ///     The three ways the declarations panel can be empty, each answered with the field that says
    ///     which it is. A panel that drew them the same would tell a reader a file declares nothing
    ///     when the truth is that nothing looked — the same distinction the import panels draw.
    /// </summary>
    [Fact]
    public async Task An_empty_declaration_list_says_which_kind_of_empty_it_is()
    {
        using var host = await DeclaringProjectAsync();

        // A language whose declarations are read, and a file that writes none.
        var none = await GetAsync<FileDeclarationsResponse>(host, Route("declarations", "one/src/Empty.cs"));
        Assert.Equal("read", none.Coverage);
        Assert.Empty(none.Declarations);

        // A language a profile covers and whose declarations this cannot read: it was never scanned,
        // which is not the same as having been scanned and found to declare nothing.
        var css = await GetAsync<FileDeclarationsResponse>(host, Route("declarations", "one/web/site.css"));
        Assert.Equal("CSS", css.LanguageName);
        Assert.Equal("unreadable", css.Coverage);
        Assert.Empty(css.Declarations);

        // An extension no profile covers: it was read with the conservative default shapes, so the
        // list is thin for a reason the panel has to be able to name.
        var uncovered = await GetAsync<FileDeclarationsResponse>(host,
            Route("declarations", "one/build/notes.rst"));
        Assert.Equal("unprofiled", uncovered.Coverage);
        Assert.Equal(".rst", uncovered.LanguageName);
        Assert.Empty(uncovered.Declarations);
    }

    /// <summary>
    ///     The boundary the panel prints as "500+", and the one the read is built around: the walk
    ///     reports what it has and stops one declaration past the ceiling, so a file declaring exactly
    ///     as many as it holds is complete and one declaring a single name more is not. The two are one
    ///     declaration apart and the answers are opposite.
    /// </summary>
    [Fact]
    public async Task A_declaration_list_that_fills_the_ceiling_is_told_apart_from_one_it_cut_short()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            // The class itself is a declaration, so one fewer routine reaches the ceiling exactly.
            ["one"] = new()
            {
                ["src/Exactly.cs"] = Routines(FileDeclarations.MaxDeclarations - 1),
                ["src/OneMore.cs"] = Routines(FileDeclarations.MaxDeclarations)
            }
        });

        var exactly = await GetAsync<FileDeclarationsResponse>(host, Route("declarations", "one/src/Exactly.cs"));
        Assert.Equal(FileDeclarations.MaxDeclarations, exactly.Declarations.Count);
        Assert.False(exactly.Capped);

        var more = await GetAsync<FileDeclarationsResponse>(host, Route("declarations", "one/src/OneMore.cs"));
        // Still only the ceiling is reported, and now the reply says the list is short of the answer.
        Assert.Equal(FileDeclarations.MaxDeclarations, more.Declarations.Count);
        Assert.True(more.Capped);
    }

    /// <summary>A C# class declaring <paramref name="count" /> routines, so <c>count + 1</c> names.</summary>
    private static string Routines(int count) =>
        "public class Big\n{\n"
        + string.Concat(Enumerable.Range(0, count).Select(i => $"    public void M{i}() {{ }}\n"))
        + "}\n";

    [Fact]
    public async Task A_path_that_names_no_file_has_no_declarations_page_rather_than_an_empty_one()
    {
        using var host = await DeclaringProjectAsync();

        using var http = host.CreateClient();
        using var response = await http.GetAsync(Route("declarations", "one/src/Nowhere.cs"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No indexed file 'one/src/Nowhere.cs'", await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    private static async Task<T> GetAsync<T>(TestHost host, string url)
    {
        using var http = host.CreateClient();
        var value = await http.GetFromJsonAsync<T>(url, Ct);
        Assert.NotNull(value);
        return value;
    }
}
