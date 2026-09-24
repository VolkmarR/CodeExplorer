using System.Net;
using System.Net.Http.Json;
using CodeExplorer.Index;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview a caller connecting to a project for the first time is told (#51): computed by the
///     build, stored as one row beside the index it describes, and carried in the durable copy so a
///     restored index answers it without a rebuild.
///     Every test here goes through the MCP tool rather than through the row, because "one call
///     orients a caller" is the acceptance criterion and a test reading the row would pass for an index
///     no tool could answer from. The one exception is the row count itself, which is what "read back as
///     one row" means.
/// </summary>
public sealed class OverviewTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    /// <summary>The local clone path <see cref="BuildAsync" /> last registered, which no reply may print.</summary>
    private string? _source;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_build_stores_one_overview_row_and_the_tool_answers_every_section_from_it()
    {
        await using var client = await BuildAsync("alpha");

        var rows = await _host.ScalarsAsync("alpha", "SELECT count(*)::VARCHAR AS n FROM project_overview");
        Assert.Equal(["1"], rows);

        string reply = await TestHost.CallAsync(client, "project_overview", []);

        // The repository and where it stands, which is what an agent needs before quoting a path.
        Assert.Contains("Repositories", reply, StringComparison.Ordinal);
        Assert.Contains("one  at ", reply, StringComparison.Ordinal);
        Assert.Contains("Languages", reply, StringComparison.Ordinal);
        Assert.Contains("Top level", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/", reply, StringComparison.Ordinal);
        Assert.Contains("Largest files", reply, StringComparison.Ordinal);
        Assert.Contains("Most changed", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Hot.prg", reply, StringComparison.Ordinal);
        // Authorship is the claim this must never make, so the heading carries the caveat with it.
        Assert.Contains("never who wrote it", reply, StringComparison.Ordinal);
        Assert.Contains("Grace <grace@example.invalid>", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extensions_group_under_their_language_and_stand_for_themselves_when_none_covers_them()
    {
        await using var client = await BuildAsync("alpha");

        string reply = await TestHost.CallAsync(client, "project_overview", []);

        // Two X# extensions, one language: the header counts with the source it belongs to rather than
        // appearing as a category of its own.
        Assert.Contains("X#", reply, StringComparison.Ordinal);
        Assert.DoesNotContain(".prg  ", reply, StringComparison.Ordinal);
        Assert.DoesNotContain(".vh ", reply, StringComparison.Ordinal);
        Assert.Contains("C#", reply, StringComparison.Ordinal);
        // An extension nobody mapped is a weaker claim than a language name, and reads as one: marked on
        // its row, and explained once in the heading rather than on every row that carries the marker.
        Assert.Contains("Languages (* an extension no language profile covers)\n", reply, StringComparison.Ordinal);
        Assert.StartsWith(".md *", Line(reply, ".md").TrimStart(), StringComparison.Ordinal);
        Assert.StartsWith(".txt *", Line(reply, ".txt").TrimStart(), StringComparison.Ordinal);
        Assert.Equal(2, reply.Split("no language profile").Length);
        Assert.DoesNotContain("X# *", reply, StringComparison.Ordinal);

        // The counts fold too, rather than the first extension's winning: three X# files, four lines.
        Assert.Contains("X#", Line(reply, "X#"), StringComparison.Ordinal);
        Assert.Contains("3 files", Line(reply, "X#"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_whose_repositories_have_no_history_still_gets_an_overview_that_says_so()
    {
        // A real index with no commit tables to rank, built end to end rather than doctored: the build
        // fills the overview, completes and swaps exactly as a refresh does, and only the history is
        // missing. An empty answer here would read as "nothing changed", the one thing history must
        // never say by accident (CONTEXT.md, History).
        await _host.CreateProjectAsync("beta");
        var builder = _host.Services.GetRequiredService<OverviewBuilder>();
        var shadow = await _host.Indexes.CreateShadowAsync("beta", Ct);
        var overview = await builder.FillAsync(shadow, false, Ct);
        await shadow.CompleteAsync(false, _ => { }, Ct);
        // Before the swap: the file cannot be moved while the shadow's connection holds it open.
        shadow.Dispose();
        await _host.Indexes.SwapShadowAsync("beta", Ct);

        Assert.Null(overview.Churn.Window());
        Assert.Empty(overview.Authors);

        await using var client = await _host.ConnectAsync("beta");
        string reply = await TestHost.CallAsync(client, "project_overview", []);

        // Both history-derived sections, and both said rather than left out: a section that vanishes
        // reads as a project nobody has worked on, which is the opposite claim.
        Assert.Contains("Most changed\n  This project's index holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Most commits, over the whole imported history (who to ask, never who wrote it)\n"
                        + "  This project's index holds no history", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_repositorys_top_level_is_in_the_tree_with_its_root_files_folded_into_one_line()
    {
        await _host.IndexedProjectAsync("gamma", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/A.cs"] = "class A;\n", ["README.md"] = "one\n", ["build.cmd"] = "echo\nbuild\n" },
            ["two"] = new() { ["lib/B.cs"] = "class B;\n" }
        });

        await using var client = await _host.ConnectAsync("gamma");
        string reply = await TestHost.CallAsync(client, "project_overview", []);

        // The cap is counted per repository, so a repository listed second cannot be squeezed out of
        // the section by how many entries the first one has at its root.
        Assert.Contains("one/src/", reply, StringComparison.Ordinal);
        Assert.Contains("two/lib/", reply, StringComparison.Ordinal);
        // Root files are one line per repository, placed with that repository's folders, and a
        // repository with none says nothing rather than "0 files at the root".
        Assert.DoesNotContain("one/README.md", Section(reply, "Top level", "Largest files"), StringComparison.Ordinal);
        string root = Line(reply, "2 files");
        Assert.Contains("3 lines", root, StringComparison.Ordinal);
        Assert.EndsWith("  at the root of one", root, StringComparison.Ordinal);
        Assert.DoesNotContain("at the root of two", reply, StringComparison.Ordinal);
        Assert.True(reply.IndexOf("one/src/", StringComparison.Ordinal) < reply.IndexOf(root, StringComparison.Ordinal)
                    && reply.IndexOf(root, StringComparison.Ordinal) < reply.IndexOf("two/lib/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_single_repository_project_folds_its_root_files_without_naming_the_repository()
    {
        await _host.IndexedProjectAsync("delta", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/A.cs"] = "class A;\n", ["README.md"] = "one\n", ["LICENSE"] = "mit\n" }
        }, singleRepository: true);

        await using var client = await _host.ConnectAsync("delta");
        string reply = await TestHost.CallAsync(client, "project_overview", []);

        Assert.Contains("  src/\n", reply, StringComparison.Ordinal);
        Assert.EndsWith("  at the root", Line(reply, "2 files"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Largest_files_ranks_only_indexed_files()
    {
        // The binary is by far the largest file, and the one the section used to lead with at 0 lines.
        await _host.IndexedProjectAsync("epsilon", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["assets/logo.bin"] = "\0\0" + new string('x', 5000), ["src/A.cs"] = "class A;\n" }
        });

        await using var client = await _host.ConnectAsync("epsilon");
        string reply = await TestHost.CallAsync(client, "project_overview", []);

        string largest = Section(reply, "Largest files", "Most changed");
        Assert.Contains("one/src/A.cs", largest, StringComparison.Ordinal);
        Assert.DoesNotContain("logo.bin", largest, StringComparison.Ordinal);
        // Languages still counts it, which is where a skipped file is reported.
        Assert.Contains("1 not indexed", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_repositories_section_names_no_local_clone_path()
    {
        await using var client = await BuildAsync("alpha");

        string reply = await TestHost.CallAsync(client, "project_overview", []);

        string row = Line(reply, "one  at ");
        Assert.EndsWith("lines", row, StringComparison.Ordinal);
        Assert.DoesNotContain(_source!, reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_restored_index_answers_the_overview_without_a_rebuild()
    {
        string built;
        await using (var client = await BuildAsync("alpha"))
            built = await TestHost.CallAsync(client, "project_overview", []);

        // What a wiped container disk leaves behind: the local file gone, the durable copy not.
        _host.DeleteIndexFile("alpha");
        Assert.False(_host.Indexes.HasIndex("alpha"));

        await using var restored = await _host.ConnectAsync("alpha");
        string reply = await TestHost.CallAsync(restored, "project_overview", []);

        Assert.Equal(built, reply);
        Assert.True(_host.Indexes.HasIndex("alpha"));
    }

    [Fact]
    public async Task The_overviews_ranking_is_the_one_hot_files_answers_with()
    {
        await using var client = await BuildAsync("alpha");

        string overview = await TestHost.CallAsync(client, "project_overview", []);
        string hot = await TestHost.CallAsync(client, "hot_files", []);

        // Called rather than reimplemented, so the two cannot drift into disagreeing about what the
        // project is busy with. The busiest file and its counts are the assertion that would break.
        string ranked = overview[overview.IndexOf("Most changed", StringComparison.Ordinal)..];
        string top = ranked.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].Trim();
        Assert.Contains("one/src/Hot.prg", top, StringComparison.Ordinal);
        Assert.Contains(top, hot.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>Patterns that would empty or change every section of the page, were the tool to read them.</summary>
    private static readonly string[] _everySection = ["**/*.prg", "**/*.md", "one/src/*"];

    /// <summary>
    ///     The excluded-paths setting is the overview page's and nothing else's (#216): the reply an
    ///     agent is given is the same byte for byte once the setting exists, and after a rebuild made
    ///     with it in place — which is what shows the build does not read it either.
    /// </summary>
    [Fact]
    public async Task The_tools_reply_is_unaffected_by_the_excluded_paths_setting()
    {
        string before;
        await using (var client = await BuildAsync("alpha"))
            before = await TestHost.CallAsync(client, "project_overview", []);

        using (var http = _host.CreateClient())
        using (var response = await http.PutAsJsonAsync("/api/projects/alpha/excluded-paths",
                   new { patterns = _everySection }, Ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using (var client = await _host.ConnectAsync("alpha"))
            Assert.Equal(before, await TestHost.CallAsync(client, "project_overview", []));

        await _host.RefreshAsync("alpha");
        await using (var client = await _host.ConnectAsync("alpha"))
            Assert.Equal(before, await TestHost.CallAsync(client, "project_overview", []));
    }

    /// <summary>The reply from one section's heading to the next's, for an assertion about that section alone.</summary>
    private static string Section(string reply, string heading, string next)
    {
        int start = reply.IndexOf("\n" + heading, StringComparison.Ordinal);
        return reply[start..reply.IndexOf("\n" + next, start + 1, StringComparison.Ordinal)];
    }

    /// <summary>The first line of the reply that starts with a given text, for an assertion about the rest of it.</summary>
    private static string Line(string reply, string name) =>
        reply.Split('\n').First(line => line.TrimStart().StartsWith(name, StringComparison.Ordinal));

    /// <summary>
    ///     A project with enough shape to have an overview worth reading: two X# extensions so the
    ///     grouping has something to fold, a C# file, an extension no profile covers, a directory and a
    ///     root file so the top level has both kinds of entry, and a history with two authors and a file
    ///     that moved more than the others.
    /// </summary>
    private async Task<McpClient> BuildAsync(string project)
    {
        const int tenDays = 10 * 24 * 60;
        string source = _source = _host.CreateEmptyGitRepository(project + "-one");
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["README.md"] = "the project\n",
                ["notes.txt"] = "notes\n",
                ["src/Hot.prg"] = "a\nb\n",
                ["src/Cold.prg"] = "cold\n",
                ["src/Defines.vh"] = "define\n",
                ["src/Api.cs"] = "class Api;\n"
            },
            "Import the code", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.prg"] = "a\nb2\n" },
            "Fix the check", "Grace", "grace@example.invalid", tenDays);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.prg"] = "a\nb3\n" },
            "Tighten it again", "Grace", "grace@example.invalid", tenDays + 1);

        await _host.CreateProjectAsync(project);
        await _host.AddRepositoryAsync(project, "one", source);
        await _host.RefreshAsync(project);
        return await _host.ConnectAsync(project);
    }
}
