using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     <c>file_history</c> and <c>blame</c>, and the two reads that carry a line of history without
///     being history tools: a grep asked for it, and a file read asked for it. They are here because
///     what they print is <c>blame</c>'s sentence, and a wording that drifted apart from it would tell
///     an agent that two tools know different things about the same line.
/// </summary>
public sealed class FileHistoryTests(FileHistoryFixture fixture) : IClassFixture<FileHistoryFixture>
{
    private readonly TestHost _host = fixture.Host;

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
    ///     The server's ceiling on one blame is a count of lines and not a last line. Read as a last
    ///     line, every range past it came back as "has no lines", which an agent takes as the file
    ///     ending there — and a 25 MiB file runs to several hundred thousand lines.
    /// </summary>
    [Fact]
    public async Task Blame_answers_a_range_past_the_first_hundred_thousand_lines()
    {
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "long",
            new Dictionary<string, string> { ["long.txt"] = string.Concat(Enumerable.Repeat("x\n", 100_010)) },
            true);

        await using var client = await _host.ConnectAsync("long");
        string reply = await TestHost.CallAsync(client, "blame",
            new Dictionary<string, object?> { ["path"] = "long.txt", ["startLine"] = 100_002, ["endLine"] = 100_004 });

        Assert.Contains("\n100002-100004 ", reply, StringComparison.Ordinal);
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
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "beta",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
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
                ["paths"] = HistoryFixtures.Paths("one/src/Check.cs"),
                ["withHistory"] = true
            });

        Assert.Contains("history: since", reply, StringComparison.Ordinal);
        Assert.Contains("last changed", reply, StringComparison.Ordinal);
        Assert.Contains("Grace", reply, StringComparison.Ordinal);
    }

    private Task<McpClient> StartAsync() => _host.ConnectAsync(HistoryFixtures.Alpha);
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class FileHistoryFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Alpha];
}
