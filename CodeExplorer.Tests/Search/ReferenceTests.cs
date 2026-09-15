using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The <c>find_references</c> tool over a project index (#11). The engine is pinned in every
///     test, as it is for grep: this tool never takes the BM25 path — a reference search is always
///     RE2 over each line — so the two engines must agree, and the shared tests run under both to
///     prove it rather than assume it.
/// </summary>
public sealed class ReferenceTests : IDisposable
{
    /// <summary>
    ///     One identifier in every shape the classifier has a name for: declared, written, called,
    ///     constructed, used as a type, read through a receiver, imported, quoted and commented.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> TwoRepositories = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] = """
                                using Domain.OrderStatus;

                                public class OrderService
                                {
                                    public OrderStatus Status { get; set; }

                                    public void Advance(OrderStatus next)
                                    {
                                        Status = next;
                                        var made = new OrderStatus();
                                        Log("OrderStatus");
                                        // OrderStatus in a comment
                                    }
                                }

                                """,
            ["src/Orders.g.cs"] = "public partial class OrderService\n{\n    public OrderStatus Generated;\n}\n"
        },
        ["two"] = new Dictionary<string, string>
        {
            ["lib/Report.cs"] = """
                                public class Report
                                {
                                    public void Run(OrderService service)
                                    {
                                        service.Advance(OrderStatus.New);
                                        var current = service.Status;
                                        service.Status = other.Status;
                                    }
                                }

                                """
        }
    };

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task Code_references_are_reported_apart_from_comments_strings_and_imports(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderStatus" });

        // The import, the string and the comment are counted in the summary and kept out of the
        // listed sections; the five code references are what is shown.
        Assert.Contains("\"OrderStatus\" in 3 files: 5 references, 3 in comments, strings or imports", text);
        Assert.DoesNotContain("COMMENTS", text);
        Assert.Contains("TYPE USES", text);
        Assert.Contains("INSTANTIATIONS", text);
        Assert.Contains("one/src/Orders.cs", text);
        Assert.Contains("two/lib/Report.cs", text);
    }

    [Fact]
    public async Task Each_shape_of_reference_gets_its_own_section()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string status = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });
        Assert.Contains("DECLARATIONS", status);
        Assert.Contains("WRITES", status);
        Assert.Contains("READS", status);

        string advance = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Advance" });
        Assert.Contains("CALLS", advance);
        Assert.Contains("two/lib/Report.cs", advance);
        Assert.Contains("DECLARATIONS", advance);
    }

    [Fact]
    public async Task A_line_naming_it_twice_yields_a_reference_for_each()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });

        // `service.Status = other.Status;` is a write and a read. Classifying the line by its first
        // occurrence alone would report it as the write and lose the read entirely.
        Assert.Contains("2 writes", text);
        Assert.Contains("2 reads", text);
        // The line itself is printed once under each of the two sections it belongs to, not twice.
        Assert.Equal(2, Occurrences(text, "service.Status = other.Status;"));
    }

    private static int Occurrences(string text, string value)
    {
        int found = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            found++;
        return found;
    }

    [Fact]
    public async Task Results_carry_the_enclosing_type_and_member()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });

        // The write sits inside Advance, which sits inside OrderService; the property declaration has
        // no enclosing member, so it is labelled with the type alone.
        Assert.Contains("[OrderService.Advance] Status = next;", text);
        Assert.Contains("[OrderService] public OrderStatus Status", text);
        Assert.Contains("[Report.Run]", text);
    }

    [Fact]
    public async Task Writes_only_answers_what_changes_this()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "Status", ["writesOnly"] = true });

        Assert.Contains("WRITES", text);
        Assert.Contains("Status = next;", text);
        Assert.DoesNotContain("READS", text);
        Assert.DoesNotContain("CALLS", text);
    }

    [Fact]
    public async Task Noise_is_listed_only_when_asked_for()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["includeNoise"] = true });

        Assert.Contains("COMMENTS", text);
        Assert.Contains("STRINGS", text);
        Assert.Contains("IMPORTS", text);
        Assert.Contains("// OrderStatus in a comment", text);
    }

    [Fact]
    public async Task Filters_scope_the_search_and_a_hidden_file_is_named_as_hidden()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string scoped = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["repo"] = "two" });
        Assert.Contains("two/lib/Report.cs", scoped);
        Assert.DoesNotContain("one/src/Orders.cs", scoped);
        // Two of the three matching files were filtered away, and a declaration could be in either.
        Assert.Contains("your filters hid 2 further matching files", scoped);

        string excluded = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["exclude"] = "*.g.cs" });
        Assert.DoesNotContain("Orders.g.cs", excluded);
        Assert.Contains("your filters hid 1 further matching file", excluded);

        string byExtension = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["ext"] = "cs", ["path"] = "one/" });
        Assert.Contains("one/src/Orders.cs", byExtension);
        Assert.DoesNotContain("two/lib/Report.cs", byExtension);
    }

    [Fact]
    public async Task A_filtered_miss_is_told_apart_from_a_name_that_is_nowhere()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string hidden = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["ext"] = "md" });
        Assert.StartsWith("Nothing in this project spells", hidden);
        Assert.Contains("outside your filters", hidden);

        string nowhere = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Unicorn" });
        Assert.StartsWith("Nothing in this project spells", nowhere);
        Assert.DoesNotContain("outside your", nowhere);
        Assert.Contains("matched whole and case-sensitively", nowhere);
    }

    [Fact]
    public async Task The_name_is_matched_whole_and_case_sensitively()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        // "Status" is a substring of "OrderStatus" and must not be found inside it; the counts below
        // are of the standalone references alone.
        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });
        Assert.DoesNotContain("new OrderStatus()", text);

        string wrongCase = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "status" });
        Assert.StartsWith("Nothing in this project spells", wrongCase);
    }

    [Fact]
    public async Task Every_reply_says_the_classification_is_textual()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderStatus" });

        Assert.Contains("Classification is textual", text);
        Assert.Contains("Strong evidence, not proof.", text);
    }

    [Fact]
    public async Task Malformed_input_is_explained_rather_than_answered_with_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string empty = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "  " });
        Assert.Contains("No symbol given", empty);

        string phrase = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "public OrderStatus" });
        Assert.Contains("is not one identifier", phrase);
        Assert.DoesNotContain("Nothing in this project spells", phrase);

        string punctuation = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "=>" });
        Assert.Contains("no identifier characters", punctuation);

        string unknownRepository = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["repo"] = "three" });
        Assert.Contains("No repository 'three'", unknownRepository);
        Assert.Contains("Repositories: one, two", unknownRepository);
    }

    [Fact]
    public async Task Only_the_files_naming_it_most_often_are_examined()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["maxFiles"] = 1 });

        Assert.Contains("3 files hold the name in total", text);
        Assert.Contains("raise maxFiles", text);
    }

    [Fact]
    public async Task A_name_too_common_to_enumerate_is_cut_short_and_says_so()
    {
        _host = new TestHost(SearchEngine.Substring);
        string many = string.Concat(Enumerable.Range(0, ReferenceSearch.MaxLinesPerFile + 5)
            .Select(i => $"var x{i} = Widget;\n"));
        await _host.IndexedProjectAsync("beta",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/Many.cs"] = many } });
        await using var client = await _host.ConnectAsync("beta");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Widget" });

        // The lines beyond the ceiling are not read, and one of them could hold the declaration.
        Assert.Contains($"one file holds {ReferenceSearch.MaxLinesPerFile} or more lines naming it", text);
        Assert.Contains("was read no further", text);
        Assert.Contains("src/Many.cs", text);
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderStatus" });

        Assert.Contains("no index to read from", text);
        Assert.Contains("POST /api/projects/alpha/refresh", text);
    }

    private async Task<McpClient> StartAsync(SearchEngine engine)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync("alpha", TwoRepositories);
        return await _host.ConnectAsync("alpha");
    }

    private static Task<string> FindAsync(McpClient client, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, "find_references", arguments);
}
