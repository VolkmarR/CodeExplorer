using CodeExplorer.Index;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview page computed live (#216): the project's excluded paths left out of every section
///     and counted, the window and the repository it is filtered to, and the switch that shows the
///     excluded paths anyway. Asserted against the JSON the browser receives, like the other operator
///     reads, and against the stored row where the claim is that nothing filtered means the same answer.
/// </summary>
public sealed class LiveOverviewTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    /// <summary>The three kinds of noise the ticket names, one of them at a repository's root.</summary>
    private static readonly string[] _noise = ["**/*.verified.txt", "**/AssemblyInfo.*", "**/*.rc"];

    [Fact]
    public async Task Excluded_paths_leave_every_section_and_are_counted_per_kind_of_section()
    {
        await NoisyProjectAsync("alpha");
        await _host.SetExcludedPathsAsync("alpha", _noise);

        var detail = await _host.OverviewDetailAsync("alpha");

        var overview = Assert.IsType<IndexOverview>(detail.Overview);
        Assert.Equal(3, detail.ExcludedPatterns);
        // Three files at HEAD, and the same three are all the history holds of them: one commit each.
        Assert.Equal(new OverviewExcluded(3, 3, 3), detail.Excluded);

        Assert.DoesNotContain(overview.Languages, l => l.Name is ".txt" or ".rc");
        Assert.Contains(overview.Languages, l => l is { Name: "C#", Files: 2 });
        var one = Assert.Single(overview.Tree, r => r.QualifiedPath == "one");
        Assert.DoesNotContain(one.Folders, f => f.QualifiedPath == "one/Properties");
        Assert.Contains(one.Folders, f => f is { QualifiedPath: "one/src", Files: 1 });
        // app.rc was the one file at the root, so the root has no entry left rather than an entry of none.
        Assert.Null(one.RootFiles);
        Assert.DoesNotContain(overview.LargestFiles, f => IsNoise(f.QualifiedPath));
        Assert.NotEmpty(overview.Churn.Files);
        Assert.DoesNotContain(overview.Churn.Files, f => IsNoise(f.QualifiedPath));
        Assert.NotEmpty(overview.Authors);
    }

    [Fact]
    public async Task The_show_excluded_switch_lifts_the_exclusions_and_counts_nothing()
    {
        await NoisyProjectAsync("alpha");
        await _host.SetExcludedPathsAsync("alpha", _noise);

        var detail = await _host.OverviewDetailAsync("alpha", "?showExcluded=true");

        var overview = Assert.IsType<IndexOverview>(detail.Overview);
        // Still told how many patterns there are, which is what offers the switch back.
        Assert.Equal(3, detail.ExcludedPatterns);
        Assert.Null(detail.Excluded);
        Assert.Contains(overview.Languages, l => l.Name == ".rc");
        Assert.Contains(overview.Churn.Files, f => f.QualifiedPath == "one/src/A.verified.txt");
    }

    [Fact]
    public async Task With_nothing_filtered_the_live_overview_is_the_stored_one()
    {
        await NoisyProjectAsync("alpha");

        var detail = await _host.OverviewDetailAsync("alpha");

        // Compared as documents, so every section and every count is compared and not a sample of them:
        // one set of statements computes both, and nothing filtered must mean nothing differs. Every
        // commit here is inside the default window, so Most commits, which the page counts over the
        // window and the stored row over all history, agrees too.
        var stored = await _host.ScalarsAsync("alpha", "SELECT document FROM project_overview");
        Assert.Equal(Assert.Single(stored), Assert.IsType<IndexOverview>(detail.Overview).ToDocument());
        Assert.Equal(0, detail.ExcludedPatterns);
        Assert.Null(detail.Excluded);
    }

    [Theory]
    // Anchored at the root of the qualified path, which in a multi-repository project is the slug.
    [InlineData("one/docs/*", new[] { "one/docs/guide.md" })]
    [InlineData("docs/*", new string[0])]
    // A leading **/ matches at any depth, the root included.
    [InlineData("**/docs/**", new[] { "one/docs/guide.md", "two/docs/notes.md", "two/lib/docs/deep.md" })]
    [InlineData("**/*.rc", new[] { "one/app.rc" })]
    // Case-insensitive, as every path filter here is.
    [InlineData("**/assemblyinfo.*", new[] { "one/Properties/AssemblyInfo.cs" })]
    // A bracket is a class, as it is to GLOB, and a dot is a dot rather than any character.
    [InlineData("**/docs/[gn]*.md", new[] { "one/docs/guide.md", "two/docs/notes.md" })]
    [InlineData("**/docs/[!gn]*.md", new[] { "two/lib/docs/deep.md" })]
    [InlineData("**/app?rc", new[] { "one/app.rc" })]
    [InlineData("**/app.r", new string[0])]
    // A leading ? is anchored like a letter, not spent on the root.
    [InlineData("?ne/docs/*", new[] { "one/docs/guide.md" })]
    // A /**/ in the middle matches no folder too, as a leading **/ matches at the root.
    [InlineData("one/**/app.rc", new[] { "one/app.rc" })]
    [InlineData("two/**/*.md", new[] { "two/docs/notes.md", "two/lib/docs/deep.md" })]
    // * crosses /, as SQL GLOB's does (ADR-0004), so an unanchored *.md reaches every depth.
    [InlineData("*.md", new[] { "one/docs/guide.md", "two/docs/notes.md", "two/lib/docs/deep.md" })]
    public async Task A_pattern_matches_qualified_paths(string pattern, string[] excluded)
    {
        await NoisyProjectAsync("alpha");
        await _host.SetExcludedPathsAsync("alpha", [pattern]);

        var detail = await _host.OverviewDetailAsync("alpha");

        Assert.Equal(excluded.Length, detail.Excluded?.Files);
        // The churn ranking lists every file here (one commit each), so what is missing from it is
        // exactly what the pattern matched.
        var ranked = Assert.IsType<IndexOverview>(detail.Overview).Churn.Files.Select(f => f.QualifiedPath);
        Assert.Equal(AllFiles.Except(excluded).Order(), ranked.Order());
    }

    [Fact]
    public async Task A_single_repository_project_matches_patterns_against_paths_with_no_slug()
    {
        await _host.IndexedProjectAsync("solo",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["solo"] = new()
                {
                    ["docs/guide.md"] = "guide\n", ["src/docs/deep.md"] = "deep\n", ["app.rc"] = "rc\n",
                    ["src/A.cs"] = "class A;\n"
                }
            }, singleRepository: true);
        await _host.SetExcludedPathsAsync("solo", ["docs/*", "**/*.rc"]);

        var detail = await _host.OverviewDetailAsync("solo");

        Assert.Equal(2, detail.Excluded?.Files);
        var ranked = Assert.IsType<IndexOverview>(detail.Overview).Churn.Files.Select(f => f.QualifiedPath);
        Assert.Equal(["src/A.cs", "src/docs/deep.md"], ranked.Order());
    }

    [Fact]
    public async Task The_window_is_anchored_to_the_newest_commit_and_the_repository_narrows_every_section()
    {
        await NoisyProjectAsync("alpha");
        // Forty days after the fixture's commits, so a thirty-day window holds this one alone.
        _host.CommitToGitRepositoryAs("two", new Dictionary<string, string> { ["lib/B.cs"] = "class B2;\n" },
            "Later", "Ada", "ada@example.invalid", 40 * 24 * 60);
        await _host.RefreshAsync("alpha");

        var month = Assert.IsType<IndexOverview>((await _host.OverviewDetailAsync("alpha", "?days=30")).Overview);
        Assert.Equal(30, month.Churn.Days);
        Assert.Equal(["two/lib/B.cs"], month.Churn.Files.Select(f => f.QualifiedPath));

        var two = Assert.IsType<IndexOverview>((await _host.OverviewDetailAsync("alpha", "?repository=two")).Overview);
        Assert.Equal(["two"], two.Tree.Select(r => r.QualifiedPath));
        Assert.All(two.LargestFiles, f => Assert.StartsWith("two/", f.QualifiedPath, StringComparison.Ordinal));
        Assert.All(two.Churn.Files, f => Assert.Equal("two", f.RepositorySlug));
        Assert.Contains(two.Languages, l => l is { Name: "C#", Files: 1 });
        Assert.Contains(two.Authors, a => a.Email == "ada@example.invalid");
    }

    [Fact]
    public async Task Most_commits_counts_the_window_on_the_page_and_all_history_in_the_stored_row()
    {
        await NoisyProjectAsync("alpha");
        // Forty days after the fixture's commits, so a thirty-day window holds this one alone.
        _host.CommitToGitRepositoryAs("two", new Dictionary<string, string> { ["lib/B.cs"] = "class B2;\n" },
            "Later", "Grace", "grace@example.invalid", 40 * 24 * 60);
        await _host.RefreshAsync("alpha");

        var month = Assert.IsType<IndexOverview>((await _host.OverviewDetailAsync("alpha", "?days=30")).Overview);
        var stored = IndexOverview.FromDocument(
            Assert.Single(await _host.ScalarsAsync("alpha", "SELECT document FROM project_overview")));

        Assert.Equal([("grace@example.invalid", 1)], month.Authors.Select(a => (a.Email, a.Commits)));
        Assert.True(stored.Authors.Count > 1);
        Assert.Contains(stored.Authors, a => a.Email == "grace@example.invalid");
    }

    [Fact]
    public async Task A_repository_the_project_does_not_have_is_said_rather_than_shown_empty()
    {
        await NoisyProjectAsync("alpha");

        var detail = await _host.OverviewDetailAsync("alpha", "?repository=nope");

        Assert.Null(detail.Overview);
        Assert.Contains("No repository 'nope'", detail.Unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_author_whose_every_commit_touched_only_excluded_paths_drops_out()
    {
        await NoisyProjectAsync("alpha");
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["src/A.verified.txt"] = "v2\n" },
            "Accept snapshots", "Snap", "snap@example.invalid", 60);
        await _host.RefreshAsync("alpha");
        await _host.SetExcludedPathsAsync("alpha", _noise);

        var overview = Assert.IsType<IndexOverview>((await _host.OverviewDetailAsync("alpha")).Overview);
        var shown = Assert.IsType<IndexOverview>((await _host.OverviewDetailAsync("alpha", "?showExcluded=true")).Overview);

        Assert.DoesNotContain(overview.Authors, a => a.Email == "snap@example.invalid");
        Assert.Contains(shown.Authors, a => a.Email == "snap@example.invalid");
    }

    [Fact]
    public async Task A_project_with_no_history_shows_the_sections_that_say_so()
    {
        await _host.HistorylessProjectAsync("beta");
        await _host.SetExcludedPathsAsync("beta", _noise);

        var detail = await _host.OverviewDetailAsync("beta", "?days=30");

        var overview = Assert.IsType<IndexOverview>(detail.Overview);
        Assert.Null(overview.Churn.Window());
        Assert.Equal(30, overview.Churn.Days);
        Assert.Empty(overview.Authors);
        Assert.Equal(new OverviewExcluded(0, 0, 0), detail.Excluded);
    }

    /// <summary>Every file <see cref="NoisyProjectAsync" /> commits, as the overview names them.</summary>
    private static readonly string[] AllFiles =
    [
        "one/src/A.cs", "one/src/A.verified.txt", "one/Properties/AssemblyInfo.cs", "one/app.rc",
        "one/docs/guide.md", "two/lib/B.cs", "two/docs/notes.md", "two/lib/docs/deep.md"
    ];

    private static bool IsNoise(string path) =>
        path.EndsWith(".verified.txt", StringComparison.Ordinal) || path.Contains("AssemblyInfo", StringComparison.Ordinal)
                                                                 || path.EndsWith(".rc", StringComparison.Ordinal);

    /// <summary>Two repositories holding <see cref="AllFiles" />, each in one commit.</summary>
    private async Task NoisyProjectAsync(string slug) =>
        await _host.IndexedProjectAsync(slug, new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/A.cs"] = "class A;\n", ["src/A.verified.txt"] = "snapshot\n",
                ["Properties/AssemblyInfo.cs"] = "[assembly: X]\n", ["app.rc"] = "resource\n",
                ["docs/guide.md"] = "guide\n"
            },
            ["two"] = new()
            {
                ["lib/B.cs"] = "class B;\n", ["docs/notes.md"] = "notes\n", ["lib/docs/deep.md"] = "deep\n"
            }
        });
}
