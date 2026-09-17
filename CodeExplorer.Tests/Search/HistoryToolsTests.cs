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
