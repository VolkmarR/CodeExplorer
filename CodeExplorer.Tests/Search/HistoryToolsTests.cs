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

    /// <summary>Routes an inline array argument through a parameter so CA1861 does not ask for a static field per call.</summary>
    private static string[] Paths(params string[] paths) => paths;

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
