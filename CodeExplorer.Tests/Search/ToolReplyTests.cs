using ModelContextProtocol;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What every MCP tool owes a caller, asserted across the whole tool surface rather than one tool
///     at a time. A contract that holds in sixteen tools and not in the seventeenth is not a contract:
///     an agent learns the shape of an answer from whichever tool it happened to call first.
/// </summary>
public sealed class ToolReplyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The host <see cref="UnbuiltProjectAsync" /> made; a test that needs its own says so.</summary>
    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    /// <summary>
    ///     A client on a project that exists and has never been built, which is where most of these
    ///     assertions belong: what a tool owes a caller about its own arguments is settled before any
    ///     index is read, so building one would only slow the suite down.
    /// </summary>
    private async Task<McpClient> UnbuiltProjectAsync()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("unbuilt");
        return await _host.ConnectAsync("unbuilt");
    }

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
        ["list_declarations"] = new() { ["path"] = "one/src/Widget.cs" },
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
        await using var client = await UnbuiltProjectAsync();

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

    /// <summary>
    ///     The three searches that read the same nullable "files matching without filters" must say a
    ///     miss with no filter on it the same way (#87). Three copies of that reading is how they drifted
    ///     apart, and an agent that learns the sentence from grep must recognise it from the other two.
    /// </summary>
    [Fact]
    public async Task The_three_searches_say_an_unfiltered_miss_in_the_same_words()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/Widget.cs"] = "class Widget\n{\n    int Size;\n}\n" } });
        await using var client = await host.ConnectAsync("alpha");

        Dictionary<string, Dictionary<string, object?>> misses = new(StringComparer.Ordinal)
        {
            ["grep"] = new() { ["query"] = "EKLfsBewKto", ["regex"] = true },
            ["find_references"] = new() { ["symbol"] = "EKLfsBewKto" },
            ["list_matches"] = new() { ["query"] = "EKLfsBewKto" }
        };

        foreach ((string tool, var arguments) in misses)
        {
            string reply = await TestHost.CallAsync(client, tool, arguments);
            Assert.Contains("no filters narrowed the search, which spanned every file.", reply,
                StringComparison.Ordinal);
            Assert.DoesNotContain("outside your filters", reply, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The near-miss spellings measured over two real agent sessions (#85): every one of them is
    ///     what the agent's own built-in file tools call the same thing, so they keep arriving. Each
    ///     entry is the tool, the name that arrived, and the required name it was meant to be.
    /// </summary>
    public static TheoryData<string, string, string> NearMisses => new()
    {
        { "glob", "pattern", "glob" },
        { "grep", "pattern", "query" },
        { "read_file", "path", "paths" },
        { "read_file", "file", "paths" }
    };

    /// <summary>
    ///     A call whose subject is spelled the way another tool spells it fails to bind, and binding
    ///     happens before the tool runs — so without this the whole reply is the transport's "An error
    ///     occurred invoking 'glob'.", which names neither the argument that was wrong nor the ones
    ///     that would have worked. Asserted through the real client, because the failure is in the
    ///     SDK's binder and a direct call on the method could not reproduce it.
    /// </summary>
    [Theory]
    [MemberData(nameof(NearMisses))]
    public async Task A_misspelled_argument_is_answered_by_naming_it_and_the_ones_the_tool_takes(
        string tool, string wrong, string required)
    {
        await using var client = await UnbuiltProjectAsync();

        string reply = await TestHost.CallAsync(client, tool, new Dictionary<string, object?> { [wrong] = "x" });

        Assert.Contains($"`{tool}` has no `{wrong}` argument", reply, StringComparison.Ordinal);
        Assert.Contains($"Required: `{required}`", reply, StringComparison.Ordinal);
        // The transport sentence is what this replaces; finding it means the filter did not fire.
        Assert.DoesNotContain("An error occurred invoking", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other half: nothing was misspelled, the required argument was simply left out. The reply
    ///     has to name it and show one, which it takes from the parameter's own <c>[Description]</c> so
    ///     that the example cannot drift from the schema the agent was given.
    /// </summary>
    [Fact]
    public async Task An_omitted_required_argument_is_answered_with_what_is_missing_and_an_example()
    {
        await using var client = await UnbuiltProjectAsync();

        string reply = await TestHost.CallAsync(client, "glob", []);

        Assert.Contains("without its required `glob` argument", reply, StringComparison.Ordinal);
        Assert.Contains("main/src/**/*Commands.cs", reply, StringComparison.Ordinal);
        Assert.Contains("Also accepts: `repo`, `limit`.", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other way a call fails before the tool runs: the name was right and the value was not a
    ///     shape the parameter can hold. It fails in the serializer rather than in the binder, and the
    ///     message it fails with names the .NET type and no parameter at all, so the reply has to work
    ///     the offender out from the schema.
    /// </summary>
    [Fact]
    public async Task A_value_of_the_wrong_shape_is_answered_by_naming_the_argument_and_what_it_holds()
    {
        await using var client = await UnbuiltProjectAsync();

        // One path rather than the array read_file takes: the shape an agent writes when it forgets
        // this tool reads several files at once.
        string one = await TestHost.CallAsync(client, "read_file",
            new Dictionary<string, object?> { ["paths"] = "one/src/Widget.cs" });
        Assert.Contains("was given `paths` as a string where it takes an array", one, StringComparison.Ordinal);

        // A word where a count belongs.
        string word = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Widget", ["context"] = "a few" });
        Assert.Contains("was given `context` as a string where it takes an integer", word,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The line either side of the one above. The SDK reads `context: "4"` as 4, and the check that
    ///     decides a value is the wrong shape has to agree with it, or the reply blames an argument that
    ///     worked. Asserted rather than assumed because the disagreement would be silent: the check
    ///     would find no fault, return nothing, and hand the transport sentence back unchanged.
    /// </summary>
    [Fact]
    public async Task A_number_written_as_a_string_still_binds()
    {
        await using var client = await UnbuiltProjectAsync();

        string reply = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Widget", ["context"] = "4" });

        Assert.Equal(IndexReader.NoIndex("unbuilt"), reply);
    }

    /// <summary>
    ///     The near-miss names are named in the reply and nowhere else (#85): accepting one would put
    ///     two spellings on a concept, which CONTEXT.md asks against. Read from the table above rather
    ///     than listed again, so that a name added there cannot be answered nicely and quietly accepted
    ///     too. Per tool and not across the surface, because a near miss is only a near miss where it
    ///     is one: `path` is the real parameter of every tool that takes a single file, and a sweep
    ///     that forbade it everywhere would have to carve that out by hand.
    /// </summary>
    [Fact]
    public async Task No_tool_accepts_the_near_miss_name_its_reply_names()
    {
        await using var client = await UnbuiltProjectAsync();

        var listed = (await client.ListToolsAsync(cancellationToken: Ct))
            .ToDictionary(tool => tool.Name, Declared, StringComparer.Ordinal);
        foreach (var row in NearMisses)
        {
            (string tool, string wrong, _) = row.Data;
            Assert.DoesNotContain(wrong, listed[tool], StringComparer.Ordinal);
        }

        // `max_results` belongs to no near-miss row because `grep` fails on its missing `query` first,
        // so it is swept for separately: it is still a name the sessions in #85 sent, and still one no
        // tool may grow.
        Assert.DoesNotContain("max_results", listed["grep"], StringComparer.Ordinal);
    }

    private static List<string> Declared(McpClientTool tool) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    /// <summary>
    ///     The failure this must not swallow. An index that cannot be read is an infrastructure failure
    ///     and throws (CODING_STANDARDS); if the filter answered it as a bad call, an agent would retry
    ///     the spelling forever against a project that will never answer. Asserted twice: once on a
    ///     clean call, and once on a call carrying a name the tool does not have, because a stray name
    ///     is the one thing present in both a bad call and this one and so the likeliest way for the
    ///     two to be confused.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failure_inside_a_tool_is_still_reported_as_a_failure(bool withAStrayArgument)
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("built",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["one"] = new() { ["src/Widget.cs"] = "class Widget { }\n" }
            });
        await host.ExecuteAsync("built", "DELETE FROM project_overview");
        await using var client = await host.ConnectAsync("built");

        var arguments = withAStrayArgument ? new Dictionary<string, object?> { ["pattern"] = "x" } : [];
        var thrown = await Assert.ThrowsAsync<McpException>(() =>
            TestHost.CallAsync(client, "project_overview", arguments));

        Assert.Contains("cannot be read", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has no", thrown.Message, StringComparison.Ordinal);
    }
}
