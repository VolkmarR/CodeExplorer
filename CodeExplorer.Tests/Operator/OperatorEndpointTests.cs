using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The reads the operator web UI is built on, and the two removals it offers. Every assertion is
///     against the JSON the browser receives, because that is the contract the UI holds.
/// </summary>
public sealed class OperatorEndpointTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Project_list_carries_the_display_name_and_the_index_status()
    {
        await _host.CreateProjectAsync("unbuilt");
        await _host.IndexedProjectAsync("built",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/A.cs"] = "class A;\nclass B;\n" } });

        var projects = await ListAsync();

        var unbuilt = Assert.Single(projects, p => p.Slug == "unbuilt");
        Assert.Equal("unbuilt", unbuilt.Name);
        Assert.Equal(0, unbuilt.Repositories);
        Assert.Null(unbuilt.Index.BuiltAt);

        var built = Assert.Single(projects, p => p.Slug == "built");
        Assert.Equal(1, built.Repositories);
        Assert.NotNull(built.Index.BuiltAt);
        Assert.Equal(1, built.Index.Files);
        Assert.Equal(2, built.Index.Lines);
    }

    [Fact]
    public async Task Project_page_reports_each_repository_with_its_commit_and_the_build_time()
    {
        await _host.IndexedProjectAsync("alpha",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/A.cs"] = "class A;\n" } });
        // Added after the build, so the page must show it as configured but not yet indexed.
        await _host.AddRepositoryAsync("alpha", "two", _host.CreateGitRepository("two", new Dictionary<string, string>
            { ["src/B.cs"] = "class B;\n" }));

        var detail = await DetailAsync("alpha");

        Assert.NotNull(detail.Index.BuiltAt);
        var one = Assert.Single(detail.Repositories, r => r.Slug == "one");
        Assert.NotNull(one.HeadCommit);
        Assert.Equal(1, one.FileCount);
        var two = Assert.Single(detail.Repositories, r => r.Slug == "two");
        Assert.Null(two.HeadCommit);
        Assert.Null(two.FileCount);
    }

    [Fact]
    public async Task A_credential_is_reported_as_set_and_never_returned()
    {
        await _host.CreateProjectAsync("alpha");
        string url = _host.CreateGitRepository("one", new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        await _host.AddRepositoryAsync("alpha", "with", url, "s3cret-token");
        await _host.AddRepositoryAsync("alpha", "without", url);

        using var http = _host.CreateClient();
        string json = await http.GetStringAsync("/api/projects/alpha", Ct);

        Assert.DoesNotContain("s3cret-token", json, StringComparison.Ordinal);
        var detail = await DetailAsync("alpha");
        Assert.True(Assert.Single(detail.Repositories, r => r.Slug == "with").HasCredential);
        Assert.False(Assert.Single(detail.Repositories, r => r.Slug == "without").HasCredential);
    }

    [Fact]
    public async Task Deleting_a_project_removes_it_from_the_list_and_takes_its_index_and_clones_with_it()
    {
        await _host.IndexedProjectAsync("alpha",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/A.cs"] = "class A;\n" } });
        string index = _host.IndexFile("alpha");
        string clones = _host.ProjectClones("alpha");
        Assert.True(File.Exists(index));
        Assert.True(Directory.Exists(clones));

        using var http = _host.CreateClient();
        using var response = await http.DeleteAsync("/api/projects/alpha", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ListAsync());
        Assert.False(File.Exists(index));
        Assert.False(Directory.Exists(clones));
    }

    [Fact]
    public async Task A_project_whose_build_was_interrupted_reads_as_not_built_and_does_not_hide_the_others()
    {
        await _host.IndexedProjectAsync("healthy",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/A.cs"] = "class A;\n" } });
        await _host.IndexedProjectAsync("broken",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/B.cs"] = "class B;\n" } });
        await _host.InterruptBuildAsync("broken");

        var projects = await ListAsync();

        Assert.Null(Assert.Single(projects, p => p.Slug == "broken").Index.BuiltAt);
        Assert.NotNull(Assert.Single(projects, p => p.Slug == "healthy").Index.BuiltAt);
    }

    [Fact]
    public async Task Deleting_an_unknown_project_says_so()
    {
        using var http = _host.CreateClient();
        using var response = await http.DeleteAsync("/api/projects/ghost", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("ghost", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_repository_leaves_the_project_and_its_other_repositories()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/A.cs"] = "class A;\n" },
            ["two"] = new() { ["src/B.cs"] = "class B;\n" }
        });

        using var http = _host.CreateClient();
        using var response = await http.DeleteAsync("/api/projects/alpha/repositories/one", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var detail = await DetailAsync("alpha");
        Assert.Equal("two", Assert.Single(detail.Repositories).Slug);
        Assert.False(Directory.Exists(_host.ClonePath("alpha", "one")));
        Assert.True(Directory.Exists(_host.ClonePath("alpha", "two")));
    }

    [Fact]
    public async Task An_unknown_project_is_a_not_found_rather_than_an_empty_page()
    {
        using var http = _host.CreateClient();
        using var response = await http.GetAsync("/api/projects/ghost", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<IReadOnlyList<ProjectSummary>> ListAsync()
    {
        using var http = _host.CreateClient();
        var projects = await http.GetFromJsonAsync<List<ProjectSummary>>("/api/projects", Ct);
        Assert.NotNull(projects);
        return projects;
    }

    private async Task<ProjectDetail> DetailAsync(string slug)
    {
        using var http = _host.CreateClient();
        var detail = await http.GetFromJsonAsync<ProjectDetail>($"/api/projects/{slug}", Ct);
        Assert.NotNull(detail);
        return detail;
    }

    [Fact]
    public async Task Project_page_reads_the_overview_the_build_stored()
    {
        await _host.IndexedProjectAsync("alpha",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["one"] = new()
                {
                    ["src/A.cs"] = "class A;\nclass B;\n",
                    ["src/Old.prg"] = "function Old()\n",
                    ["README.md"] = "read me\n"
                }
            });

        var detail = await OverviewAsync("alpha");

        Assert.NotNull(detail.Overview);
        // The same grouping the tool reports, from the same row: C# and X# by language, and the
        // extension no profile covers standing for itself and saying so.
        Assert.Contains(detail.Overview.Languages, l => l is { Name: "C#", Mapped: true, Files: 1 });
        Assert.Contains(detail.Overview.Languages, l => l is { Name: "X#", Mapped: true, Files: 1 });
        Assert.Contains(detail.Overview.Languages, l => l is { Name: ".md", Mapped: false });
        // A directory and a file at the root, which is what the top level has to be able to show.
        Assert.Contains(detail.Overview.Tree, e => e is { QualifiedPath: "one/src", IsDirectory: true });
        Assert.Contains(detail.Overview.Tree, e => e is { QualifiedPath: "one/README.md", IsDirectory: false });
        Assert.NotEmpty(detail.Overview.LargestFiles);
        Assert.NotNull(detail.Overview.Churn.Until);
        Assert.NotEmpty(detail.Overview.Authors);
    }

    [Fact]
    public async Task A_project_with_no_index_has_no_overview_rather_than_an_empty_one()
    {
        await _host.CreateProjectAsync("unbuilt");

        var detail = await OverviewAsync("unbuilt");

        // Null and not an overview of nothing: every project passes through this state, and empty
        // sections would read as a project that really is empty. The prose comes with it, so the page
        // says what an agent asking the same question is told rather than leaving a blank.
        Assert.Null(detail.Overview);
        Assert.NotNull(detail.Unavailable);
        Assert.Contains("has no index to read from", detail.Unavailable, StringComparison.Ordinal);
    }

    private async Task<ProjectOverviewDetail> OverviewAsync(string slug)
    {
        using var http = _host.CreateClient();
        var detail = await http.GetFromJsonAsync<ProjectOverviewDetail>($"/api/projects/{slug}/overview", Ct);
        Assert.NotNull(detail);
        return detail;
    }
}
