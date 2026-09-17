using System.Globalization;
using System.Net.Http.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The history tools as an agent reaches them: <c>git_log</c>, <c>file_history</c> and
///     <c>blame</c>, over MCP and against a real index. None of them matches text, so the engine is
///     pinned to Substring once rather than run twice.
///     What is asserted here is mostly the prose. These tools answer a question an agent will act on —
///     who to ask about a line — and a reply that reads as authorship when it means "last touched" is
///     the failure mode, not a wrong row.
/// </summary>
public sealed class HistoryToolsTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Git_log_lists_the_commits_newest_first_with_their_authors()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "git_log", []);

        Assert.Contains("2 commits", reply, StringComparison.Ordinal);
        Assert.Contains("Grace <grace@example.invalid>", reply, StringComparison.Ordinal);
        Assert.Contains("Tighten the check", reply, StringComparison.Ordinal);
        // Newest first: the second commit's subject must precede the first's.
        Assert.True(reply.IndexOf("Tighten the check", StringComparison.Ordinal)
                    < reply.IndexOf("Add the validator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task File_history_lists_the_commits_that_changed_one_file()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "file_history",
            new Dictionary<string, object?> { ["path"] = "one/src/Check.cs" });

        Assert.Contains("2 commits changed one/src/Check.cs", reply, StringComparison.Ordinal);
        Assert.Contains("Ada <ada@example.invalid>", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blame_groups_consecutive_lines_and_names_who_last_changed_them()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "blame",
            new Dictionary<string, object?> { ["path"] = "one/src/Check.cs" });

        // Line 2 is its own run between two lines the first commit still owns, which is the shape that
        // proves the runs are built from the attribution and not from the file.
        Assert.Contains("Tighten the check", reply, StringComparison.Ordinal);
        Assert.Contains("Grace", reply, StringComparison.Ordinal);
        Assert.Contains("\n2  ", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A miss must not read like a real answer: a path that names no file is a sentence saying so,
    ///     never an empty blame that an agent would take to mean "nobody has ever touched this".
    /// </summary>
    [Fact]
    public async Task An_unknown_path_is_an_answer_and_not_an_empty_result()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "blame",
            new Dictionary<string, object?> { ["path"] = "one/src/Missing.cs" });

        Assert.Contains("No indexed file", reply, StringComparison.Ordinal);
        Assert.Contains("glob or list_tree", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The distinction the whole tool surface rests on. A project whose index holds no history must
    ///     say so, because "no commits" and "nobody changed this" read identically to an agent and mean
    ///     opposite things (CODING_STANDARDS, Errors).
    /// </summary>
    [Fact]
    public async Task A_project_without_history_says_so_rather_than_answering_nothing()
    {
        await _host.IndexedProjectAsync("beta",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["only"] = new() { ["a.cs"] = "class A;\n" }
            });
        // The fixture's own commits are real history, so the tables are emptied to make the index look
        // like one built before history was imported — which is exactly what every existing index is.
        await _host.ExecuteAsync("beta", "DELETE FROM commits");

        await using var client = await _host.ConnectAsync("beta");
        string reply = await TestHost.CallAsync(client, "git_log", []);

        Assert.Contains("holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Ask the operator to refresh", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The opt-in on the tools that already existed. Default off is the contract: these replies are
    ///     rationed on payload, and attribution on every line of every search would spend that budget on
    ///     a question most searches are not asking.
    /// </summary>
    [Fact]
    public async Task Grep_annotates_lines_only_when_history_is_asked_for()
    {
        var client = await StartAsync();

        string plain = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "second-changed" });
        Assert.DoesNotContain("Grace", plain, StringComparison.Ordinal);

        string annotated = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "second-changed", ["withHistory"] = true });
        Assert.Contains("Grace", annotated, StringComparison.Ordinal);
        // The code still starts where it did; the attribution is appended, not prepended.
        Assert.Contains("2: second-changed", annotated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_file_adds_one_history_line_per_file_when_asked()
    {
        var client = await StartAsync();

        string reply = await TestHost.CallAsync(client, "read_file",
            new Dictionary<string, object?>
            {
                ["paths"] = Paths("one/src/Check.cs"),
                ["withHistory"] = true
            });

        Assert.Contains("history: since", reply, StringComparison.Ordinal);
        Assert.Contains("last changed", reply, StringComparison.Ordinal);
        Assert.Contains("Grace", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The ranking itself: most commits first, with the lines each file gained and lost. Written
    ///     against a fixture whose busiest file is not its largest, so a ranking that fell back to size
    ///     or to path order would fail rather than happen to agree.
    /// </summary>
    [Fact]
    public async Task Hot_files_ranks_the_files_the_window_changed_most()
    {
        await using var client = await ChurnAsync();
        string reply = await TestHost.CallAsync(client, "hot_files", []);

        Assert.Contains("most-changed", reply, StringComparison.Ordinal);
        // Three commits touched Hot.cs, two Cold.cs, one Note.md, and the order must be that.
        Assert.True(reply.IndexOf("one/src/Hot.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/src/Cold.cs", StringComparison.Ordinal));
        Assert.True(reply.IndexOf("one/src/Cold.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/docs/Note.md", StringComparison.Ordinal));
        Assert.Contains("3 commits", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The window is measured back from the newest recorded commit and not from today, which is the
    ///     decision the next two tickets inherit: an index built a month ago must still answer "the last
    ///     week" with the last week it holds, rather than with nothing.
    /// </summary>
    [Fact]
    public async Task Hot_files_measures_the_window_back_from_the_newest_recorded_commit()
    {
        await using var client = await ChurnAsync();

        // The fixture's newest commits are ten days after its first. A day-wide window reaches the
        // recent ones and not the old one, although every one of them is decades before today.
        string recent = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 1 });
        Assert.Contains("one/src/Hot.cs", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("one/old/Ancient.cs", recent, StringComparison.Ordinal);

        string wide = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });
        Assert.Contains("one/old/Ancient.cs", wide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hot_files_scopes_to_a_directory_and_to_a_repository()
    {
        await using var client = await ChurnAsync();

        string scoped = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "one/src" });
        Assert.Contains("one/src/Hot.cs", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("Note.md", scoped, StringComparison.Ordinal);

        // A bare slug is the whole repository, which is the same answer with the docs back in it.
        string repository = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "one" });
        Assert.Contains("repository 'one'", repository, StringComparison.Ordinal);
        Assert.Contains("one/docs/Note.md", repository, StringComparison.Ordinal);

        string unknown = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "nowhere/src" });
        Assert.Contains("No repository 'nowhere'", unknown, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A path the window changed and HEAD no longer holds is ranked and marked. Leaving it out
    ///     would understate the churn of the area it was in; leaving it unmarked would send an agent to
    ///     read a file that is not there.
    /// </summary>
    [Fact]
    public async Task Hot_files_marks_a_path_that_is_no_longer_at_head()
    {
        await using var client = await ChurnAsync();
        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });

        Assert.Contains("one/src/Gone.cs  (no longer at HEAD)", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Hot.cs\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The overview's ranking and <c>hot_files</c>' are the same rows, mark and all. An agent reads
    ///     the overview to orient itself and then calls the tool for more, so a row that changed shape
    ///     between the two reads as a different fact — and the mark is the half that matters: one
    ///     surface forgetting it sends the agent to open a file that is not there.
    /// </summary>
    [Fact]
    public async Task The_overview_ranks_in_the_rows_hot_files_prints_down_to_the_mark_on_a_deleted_path()
    {
        await using var client = await ChurnAsync();

        string overview = await TestHost.CallAsync(client, "project_overview", []);
        string hot = await TestHost.CallAsync(client, "hot_files", []);

        // The overview indents its sections; the rows are otherwise drawn by the same helper, which is
        // what this compares. Taken from the overview because it is the shorter of the two rankings.
        var rows = overview[overview.IndexOf("Most changed", StringComparison.Ordinal)..]
            .Split('\n')
            .Skip(1)
            .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))
            .Select(line => line[2..])
            .ToList();

        Assert.Contains(rows, row => row.EndsWith("one/src/Gone.cs  (no longer at HEAD)", StringComparison.Ordinal));
        foreach (string row in rows) Assert.Contains(row + "\n", hot, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tool and the page read one history. They page it differently — the page counts what it
    ///     has not shown, the tool does not — but a caller comparing the two is comparing one scope, and
    ///     a scope that meant different things on the two surfaces is the drift this pins.
    /// </summary>
    [Fact]
    public async Task Git_log_and_the_change_log_page_agree_about_a_repository()
    {
        await BuildChurnProjectAsync("agree", true);
        await using var client = await _host.ConnectAsync("agree");

        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<CommitListResponse>("/api/projects/agree/commits?repository=one", TestContext.Current.CancellationToken);
        Assert.NotNull(page);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repository"] = "one" });

        // Five commits in the fixture's first repository and one in its second: a tool that dropped the
        // scope, or a page that kept it, would disagree here rather than both saying five.
        Assert.Equal(5, page.Total);
        Assert.Contains($"{page.Total} commits in repository 'one'", reply, StringComparison.Ordinal);
        // Both are newest first, so the same commit heads each.
        Assert.Contains(page.Commits[0].Sha[..8], reply, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Other.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two repositories, one of which was never walked. The ranking cannot show what it has not got,
    ///     so it has to say which repositories it is speaking for — otherwise it reads as the project's
    ///     whole story (CONTEXT.md, History).
    /// </summary>
    [Fact]
    public async Task Hot_files_says_which_repositories_have_history_and_which_have_none()
    {
        await BuildChurnProjectAsync("gamma", withSecondRepository: true);
        // What a repository whose walk found nothing leaves behind: files in the index, no commits.
        await _host.ExecuteAsync("gamma", "DELETE FROM commits WHERE repo_slug = 'two'");

        await using var client = await _host.ConnectAsync("gamma");
        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });

        Assert.Contains("History was imported for one and for none of two", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("two/src/Other.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A single-repository project names its files without a slug (ADR-0006), and a ranking that
    ///     spelled one in would hand back paths every other tool refuses.
    /// </summary>
    [Fact]
    public async Task Hot_files_names_paths_the_way_a_single_repository_project_does()
    {
        await _host.IndexedProjectAsync("solo", new Dictionary<string, Dictionary<string, string>>
            { ["only"] = new() { ["src/Widget.cs"] = "class Widget { }\n" } }, true);

        await using var client = await _host.ConnectAsync("solo");
        string reply = await TestHost.CallAsync(client, "hot_files", []);

        Assert.Contains(" src/Widget.cs\n", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("only/src/Widget.cs", reply, StringComparison.Ordinal);

        // And it takes a directory in the same spelling, with no slug to strip off first.
        string scoped = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "src" });
        Assert.Contains(" src/Widget.cs\n", scoped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hot_files_on_a_project_without_history_says_so_rather_than_ranking_nothing()
    {
        await _host.IndexedProjectAsync("delta",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["only"] = new() { ["a.cs"] = "class A;\n" }
            });
        await _host.ExecuteAsync("delta", "DELETE FROM commits");

        await using var client = await _host.ConnectAsync("delta");
        string reply = await TestHost.CallAsync(client, "hot_files", []);

        Assert.Contains("holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Ask the operator to refresh", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The coupling itself: the files a window's commits kept changing alongside one file, most
    ///     shared commits first. The fixture couples Api.cs to Store.cs more often than to Dto.cs and
    ///     never to Lonely.cs, so a ranking that counted the window's commits rather than the shared
    ///     ones would fail rather than happen to agree.
    /// </summary>
    [Fact]
    public async Task Co_changed_ranks_the_files_that_keep_moving_with_a_file()
    {
        await BuildCoupledProjectAsync(_host, "coupled");
        await using var client = await _host.ConnectAsync("coupled");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });

        // Three commits changed Store.cs with Api.cs, two Dto.cs, one Old.cs, and the order must be that.
        Assert.True(reply.IndexOf("one/src/Store.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/src/Dto.cs", StringComparison.Ordinal));
        Assert.True(reply.IndexOf("one/src/Dto.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/src/Old.cs", StringComparison.Ordinal));
        Assert.Contains("3 shared commits", reply, StringComparison.Ordinal);
        // Lonely.cs was committed on its own, so no commit of Api.cs's could have carried it.
        Assert.DoesNotContain("Lonely.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A path the window changed and HEAD no longer holds is ranked here for the reason it is ranked
    ///     in hot_files: it is coupling that happened, and an agent sent to read a file that is not
    ///     there has been told something false.
    /// </summary>
    [Fact]
    public async Task Co_changed_marks_a_path_that_is_no_longer_at_head()
    {
        await BuildCoupledProjectAsync(_host, "gone");
        await using var client = await _host.ConnectAsync("gone");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });

        Assert.Contains("one/src/Old.cs  (no longer at HEAD)", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Store.cs\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The whole risk in this ranking. One reformat, vendor drop or initial import pairs every path
    ///     it touched with every other, and those pairs are not coupling — they are one commit. The
    ///     ceiling keeps them out, and because what counts as a mass commit differs between a repository
    ///     of two hundred files and one of eighty thousand, it is a setting: the same fixture answers
    ///     differently on a host that was told a dozen paths is a mass commit.
    /// </summary>
    [Fact]
    public async Task Co_changed_leaves_a_mass_commit_out_of_the_pairing_and_says_it_did()
    {
        await BuildCoupledProjectAsync(_host, "bulk");
        await using var wide = await _host.ConnectAsync("bulk");
        string included = await TestHost.CallAsync(wide, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });
        // Under the shipped ceiling the reformat is an ordinary commit, and every path it touched
        // is coupled to Api.cs once — which is what swamps the ranking and what the ceiling is for.
        Assert.Contains("vendor/Bulk01.cs", included, StringComparison.Ordinal);
        Assert.DoesNotContain("left out of the pairing", included, StringComparison.Ordinal);

        using var tight = new TestHost(SearchEngine.Substring, maxCommitPaths: 5);
        await BuildCoupledProjectAsync(tight, "bulk");
        await using var client = await tight.ConnectAsync("bulk");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });

        Assert.DoesNotContain("vendor/Bulk", reply, StringComparison.Ordinal);
        Assert.Contains("left out of the pairing", reply, StringComparison.Ordinal);
        Assert.Contains("History:MaxCommitPaths", reply, StringComparison.Ordinal);
        // The coupling that is real survives the exclusion.
        Assert.Contains("one/src/Store.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A file nothing moves with must say so. An empty list reads as "this file has no couplings
    ///     worth knowing", which is a finding, and it must not be what a caller gets from a file whose
    ///     history simply has not been imported (CODING_STANDARDS, Errors).
    /// </summary>
    [Fact]
    public async Task Co_changed_says_a_file_moves_alone_rather_than_answering_nothing()
    {
        await BuildCoupledProjectAsync(_host, "alone");
        await using var client = await _host.ConnectAsync("alone");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Lonely.cs", ["days"] = 30 });

        Assert.Contains("No other file", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Lonely.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A window that reaches none of a file's commits is not a file that moves alone, and the two
    ///     answers have to read differently or the shorter window teaches the agent a wrong fact.
    /// </summary>
    [Fact]
    public async Task Co_changed_says_when_the_window_reached_none_of_a_files_commits()
    {
        await BuildCoupledProjectAsync(_host, "narrow");
        await using var client = await _host.ConnectAsync("narrow");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/old/Ancient.cs", ["days"] = 1 });

        Assert.Contains("No commit", reply, StringComparison.Ordinal);
        Assert.Contains("raise days", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Co_changed_on_a_project_without_history_says_so_rather_than_pairing_nothing()
    {
        await _host.IndexedProjectAsync("epsilon",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["only"] = new() { ["a.cs"] = "class A;\n" }
            });
        await _host.ExecuteAsync("epsilon", "DELETE FROM commits");

        await using var client = await _host.ConnectAsync("epsilon");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "only/a.cs" });

        Assert.Contains("holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Ask the operator to refresh", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Co_changed_on_an_unknown_path_is_an_answer_and_not_an_empty_ranking()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Missing.cs" });

        Assert.Contains("No indexed file", reply, StringComparison.Ordinal);
        Assert.Contains("glob or list_tree", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A repository with coupling to find. Api.cs moves with Store.cs three times, with Dto.cs
    ///     twice and with Old.cs once before that file is deleted; Lonely.cs moves on its own; and one
    ///     reformat touches Api.cs and a dozen vendored paths at once, which is the mass commit the
    ///     ceiling exists for. Ancient.cs sits ten days before the rest so a narrow window can miss it.
    /// </summary>
    private static async Task BuildCoupledProjectAsync(TestHost host, string project)
    {
        const int tenDays = 10 * 24 * 60;
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["old/Ancient.cs"] = "one\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string>
            {
                ["src/Api.cs"] = "a\n",
                ["src/Dto.cs"] = "d\n",
                ["src/Old.cs"] = "o\n",
                ["src/Store.cs"] = "s\n"
            },
            "Add the feature", "Ada", "ada@example.invalid", tenDays);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a2\n", ["src/Store.cs"] = "s2\n" },
            "Extend the feature", "Grace", "grace@example.invalid", tenDays + 1);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a3\n", ["src/Store.cs"] = "s3\n" },
            "Extend it again", "Grace", "grace@example.invalid", tenDays + 2);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a4\n", ["src/Dto.cs"] = "d2\n" },
            "Reshape the payload", "Grace", "grace@example.invalid", tenDays + 3);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Lonely.cs"] = "l\n" },
            "Add a file nothing moves with", "Ada", "ada@example.invalid", tenDays + 4);

        var reformat = new Dictionary<string, string> { ["src/Api.cs"] = "a5\n" };
        for (int i = 1; i <= 12; i++)
            reformat[string.Create(CultureInfo.InvariantCulture, $"vendor/Bulk{i:00}.cs")] = $"bulk {i}\n";
        host.CommitToGitRepositoryAs(name, reformat, "Reformat everything", "Ada", "ada@example.invalid",
            tenDays + 5);
        host.RemoveInGitRepositoryAs(name, ["src/Old.cs"], "Drop the old file", "Grace",
            "grace@example.invalid", tenDays + 6);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>Routes an inline array argument through a parameter so CA1861 does not ask for a static field per call.</summary>
    private static string[] Paths(params string[] paths) => paths;

    /// <summary>
    ///     A repository with something to rank: one old commit, then four ten days later that touch
    ///     three files unequally, then one that deletes a fourth. Every date is decades before today, so
    ///     a window measured from the clock rather than from the history would rank nothing at all.
    /// </summary>
    private async Task<McpClient> ChurnAsync()
    {
        await BuildChurnProjectAsync("churn", false);
        return await _host.ConnectAsync("churn");
    }

    /// <inheritdoc cref="ChurnAsync" />
    private async Task BuildChurnProjectAsync(string project, bool withSecondRepository)
    {
        const int tenDays = 10 * 24 * 60;
        string source = _host.CreateEmptyGitRepository(project + "-one");
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["old/Ancient.cs"] = "one\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["docs/Note.md"] = "note\n",
                ["src/Cold.cs"] = "cold\n",
                ["src/Gone.cs"] = "gone\n",
                ["src/Hot.cs"] = "a\nb\nc\n"
            },
            "Add the module", "Ada", "ada@example.invalid", tenDays);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb2\nc\n" },
            "Fix the check", "Grace", "grace@example.invalid", tenDays + 1);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb3\nc\n", ["src/Cold.cs"] = "cold2\n" },
            "Tighten it again", "Grace", "grace@example.invalid", tenDays + 2);
        _host.RemoveInGitRepositoryAs(project + "-one", ["src/Gone.cs"], "Drop the dead file", "Grace",
            "grace@example.invalid", tenDays + 3);

        await _host.CreateProjectAsync(project);
        await _host.AddRepositoryAsync(project, "one", source);
        if (withSecondRepository)
            await _host.AddRepositoryAsync(project, "two",
                _host.CreateGitRepository(project + "-two", new Dictionary<string, string>
                    { ["src/Other.cs"] = "other\n" }));
        await _host.RefreshAsync(project);
    }

    private async Task<McpClient> StartAsync()
    {
        string source = _host.CreateEmptyGitRepository("one");
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond\nthird\n" },
            "Add the validator", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nthird\n" },
            "Tighten the check", "Grace", "grace@example.invalid", 1);

        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", source);
        await _host.RefreshAsync("alpha");
        return await _host.ConnectAsync("alpha");
    }
}
