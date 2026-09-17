using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What every MCP tool owes a caller, asserted across the whole tool surface rather than one tool
///     at a time. A contract that holds in sixteen tools and not in the seventeenth is not a contract:
///     an agent learns the shape of an answer from whichever tool it happened to call first.
/// </summary>
public sealed class ToolReplyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Every tool, with arguments good enough to reach the index. The table is compared against what
    ///     the server actually lists, so a tool added without an entry here fails rather than quietly
    ///     escaping the assertion below.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, object?>> Tools = new(StringComparer.Ordinal)
    {
        ["blame"] = new() { ["path"] = "one/src/Widget.cs" },
        ["co_changed"] = new() { ["path"] = "one/src/Widget.cs" },
        ["file_history"] = new() { ["path"] = "one/src/Widget.cs" },
        ["find_definition"] = new() { ["symbol"] = "Widget" },
        ["find_references"] = new() { ["symbol"] = "Widget" },
        ["git_log"] = [],
        ["glob"] = new() { ["glob"] = "*.cs" },
        ["grep"] = new() { ["query"] = "Widget" },
        ["hot_files"] = [],
        ["imports"] = new() { ["path"] = "one/src/Widget.cs" },
        ["list_extensions"] = [],
        ["list_matches"] = new() { ["query"] = "Widget" },
        ["list_tree"] = [],
        ["project_overview"] = [],
        ["read_file"] = new() { ["paths"] = new[] { "one/src/Widget.cs" } },
        ["repo_info"] = [],
        ["which_project"] = [],
        ["who_imports"] = new() { ["path"] = "one/src/Widget.cs" }
    };

    /// <summary>
    ///     The one tool that answers without reading the index: it says which project the endpoint is
    ///     bound to, which is true before anything is built.
    /// </summary>
    private const string WithoutIndex = "which_project";

    /// <summary>
    ///     A project that exists and has never been built is the state every project passes through, and
    ///     an agent meets it whenever an operator adds one. It is an answer and not a failure
    ///     (CODING_STANDARDS): a tool that threw would reach the agent as a protocol error, which is the
    ///     one shape it cannot read the explanation out of.
    /// </summary>
    [Fact]
    public async Task Every_tool_that_reads_the_index_says_a_project_was_never_built_in_the_same_words()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("unbuilt");
        await using var client = await host.ConnectAsync("unbuilt");

        var listed = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Order().ToList();
        // The table above is the assertion's scope, so it has to be the server's whole surface.
        Assert.Equal(Tools.Keys.Order(), listed);

        string expected = IndexReader.NoIndex("unbuilt");
        foreach (string tool in listed.Where(name => name != WithoutIndex))
        {
            // CallAsync re-raises a refusal as an McpException, so reaching an assertion at all is what
            // proves the tool answered rather than threw.
            string reply = await TestHost.CallAsync(client, tool, Tools[tool]);
            Assert.StartsWith(expected, reply, StringComparison.Ordinal);
        }
    }
}
