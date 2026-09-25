using CodeExplorer.Index;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The <c>list_matches</c> tool over a project index (#11). The engine is pinned in every test,
///     as it is for grep: extraction is always RE2 and never BM25, so the two engines must agree, and
///     the shared test runs under both to prove it rather than assume it.
/// </summary>
public sealed class MatchListTests : IDisposable
{
    private static readonly Dictionary<string, Dictionary<string, string>> TwoRepositories = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Api.csproj"] = """
                                 <Project>
                                   <ItemGroup>
                                     <PackageReference Include="Serilog" />
                                     <PackageReference Include="Dapper" />
                                   </ItemGroup>
                                 </Project>

                                 """,
            ["src/Orders.cs"] = "var a = Status.Open;\nvar b = Status.Open;\nvar c = Status.Closed;\n"
        },
        ["two"] = new Dictionary<string, string>
        {
            ["lib/Jobs.csproj"] = "<Project>\n  <PackageReference Include=\"Serilog\" />\n</Project>\n",
            ["lib/Jobs.cs"] = "var d = Status.Open;\n"
        }
    };

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task Distinct_capture_group_values_come_back_with_counts_most_frequent_first(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string text = await ListAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "PackageReference Include=\"([^\"]+)\"", ["group"] = 1 });

        Assert.Contains("2 distinct values from 3 matches in 2 files", text);
        Assert.Contains("count  files  value", text);
        // Serilog occurs twice in two files and Dapper once, so Serilog leads.
        int serilog = text.IndexOf("Serilog", StringComparison.Ordinal);
        int dapper = text.IndexOf("Dapper", StringComparison.Ordinal);
        Assert.True(serilog > 0 && serilog < dapper, text);
        Assert.Contains("    2      2  Serilog", text);
        Assert.Contains("    1      1  Dapper", text);
    }

    [Fact]
    public async Task Group_zero_returns_the_whole_match()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "Status\\.\\w+" });

        Assert.Contains("2 distinct values from 4 matches in 2 files", text);
        Assert.Contains("    3      2  Status.Open", text);
        Assert.Contains("    1      1  Status.Closed", text);
    }

    [Fact]
    public async Task Filters_scope_the_listing_to_a_repository_a_path_and_an_extension()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string scoped = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.(\\w+)", ["group"] = 1, ["repo"] = "two" });
        Assert.Contains("1 distinct value", scoped);
        Assert.DoesNotContain("Closed", scoped);

        string byExtension = await ListAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Include=\"([^\"]+)\"", ["group"] = 1, ["ext"] = "csproj", ["path"] = "one/" });
        Assert.Contains("Dapper", byExtension);
        Assert.Contains("1 distinct value", scoped);

        string excluded = await ListAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Include=\"([^\"]+)\"", ["group"] = 1, ["exclude"] = "two/" });
        Assert.Contains("2 distinct values from 2 matches in 1 file", excluded);
    }

    [Fact]
    public async Task A_filtered_miss_is_told_apart_from_a_pattern_that_matches_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string hidden = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.(\\w+)", ["group"] = 1, ["ext"] = "csproj" });
        Assert.StartsWith("No matches", hidden);
        Assert.Contains("does match in", hidden);
        Assert.Contains("outside your filters", hidden);

        // Asserted as the whole sentence, not as an absence: a reply that stopped saying what it
        // searched would still hold "does not contain 'outside your'" (#87).
        string nowhere = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "Unicorn\\w+" });
        Assert.StartsWith("No matches", nowhere);
        Assert.DoesNotContain("outside your", nowhere);
        Assert.Contains("no filters narrowed the search, which spanned every file.", nowhere);
        Assert.Contains("Try the same pattern with grep", nowhere);
    }

    /// <summary>
    ///     An empty capture group is dropped before the grouping, so a pattern that matched every line
    ///     with an empty group comes back exactly as one that matched nothing. The reply may not pick
    ///     one of the two: the caller would go and fix whichever it named (#87).
    /// </summary>
    [Fact]
    public async Task A_pattern_whose_group_is_always_empty_is_not_reported_as_matching_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        // The same pattern through grep, so the case is the real one: it does match, and only the
        // group is empty. Without this the assertions below would also hold for a pattern matching
        // nothing, which is the case they exist to tell apart.
        string matching = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Status\\.(x*)Open", ["regex"] = true });
        Assert.Contains("files match in total", matching);

        string text = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.(x*)Open", ["group"] = 1 });

        Assert.StartsWith("No matches", text);
        // Both halves of the disjunction, and neither as certain: this call is the case where the
        // second half is the true one.
        Assert.Contains(
            "The pattern matched nothing, or matched but capture group 1 was always empty; no filters narrowed the search, which spanned every file.",
            text);
        Assert.Contains("Try the same pattern with grep", text);
    }

    [Fact]
    public async Task Whole_word_anchors_the_pattern_without_shifting_the_group_numbers()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        // The anchoring wraps the pattern in a non-capturing group, so group 1 is still the caller's.
        string text = await ListAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Status\\.(\\w+)", ["group"] = 1, ["wholeWord"] = true });

        Assert.Contains("Open", text);
        Assert.Contains("Closed", text);
    }

    /// <summary>
    ///     Whole words bounded by letters outside ASCII (#235), and counted where two sit one character
    ///     apart: the boundary consumes that character, and neither neighbour may lose it.
    /// </summary>
    [Fact]
    public async Task Whole_words_are_bounded_by_letters_outside_ascii_and_counted_side_by_side()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("umlaut", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Words.cs"] = "Ändern(bar,bar);\nvar x = fooÄbar + Status.Offen;\n"
            }
        });
        await using var client = await _host.ConnectAsync("umlaut");

        string words = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "bar|Ändern", ["wholeWord"] = true });
        Assert.Contains("2 distinct values from 3 matches in 1 file", words);
        Assert.Contains("    2      1  bar", words);
        Assert.Contains("    1      1  Ändern", words);

        string grouped = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.(\\w+)", ["group"] = 1, ["wholeWord"] = true });
        Assert.Contains("Offen", grouped);
    }

    [Fact]
    public async Task A_missing_capture_group_is_named_rather_than_answered_with_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.\\w+", ["group"] = 1 });

        Assert.Contains("no capture groups", text);
        Assert.Contains("group=1", text);
        Assert.DoesNotContain("No matches", text);
    }

    /// <summary>
    ///     A named group captures in RE2 like any other, so it is counted as one (#238). The .NET
    ///     spelling is one the bundled RE2 rejects, and the reply says which spelling it takes.
    /// </summary>
    [Fact]
    public async Task A_named_capture_group_is_extracted()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.(?P<name>\\w+)", ["group"] = 1 });
        Assert.Contains("2 distinct values from 4 matches in 2 files", text);
        Assert.Contains("    3      2  Open", text);
        Assert.Contains("    1      1  Closed", text);

        string dotnet = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.(?<name>\\w+)", ["group"] = 1 });
        Assert.Contains("not a valid RE2", dotnet);
        Assert.Contains("Name a group as (?P<name>...).", dotnet);

        // An optional literal parenthesis before a '<' is not that spelling, nor is one before '='
        // a lookahead.
        string literal = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.\\(?<?=?(?P<name>\\w+)", ["group"] = 1 });
        Assert.Contains("    3      2  Open", literal);
    }

    /// <summary>
    ///     A parenthesis quoted by <c>\Q...\E</c> or escaped or inside a class is a literal and opens no
    ///     group, so asking for group 1 is refused by name rather than handed to the engine (#238).
    /// </summary>
    [Theory]
    [InlineData("\\Q(\\E")]
    [InlineData("\\Q(")]
    [InlineData("\\(")]
    [InlineData("[(]")]
    [InlineData("[](]")]
    public async Task A_literal_parenthesis_opens_no_capture_group(string query)
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await ListAsync(client, new Dictionary<string, object?> { ["query"] = query, ["group"] = 1 });

        Assert.Contains("The pattern has no capture groups, so group=1 cannot be extracted.", text);
    }

    /// <summary>
    ///     Wrapped as a whole word, <c>a)|(b</c> would balance into a pattern with a group of its own;
    ///     alone it is no pattern at all, and a <c>\Q</c> left open is a literal to the end (#238).
    /// </summary>
    [Fact]
    public async Task A_whole_word_pattern_means_what_it_meant_alone()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string unbalanced = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "a)|(b", ["wholeWord"] = true });
        Assert.Contains("not a valid RE2", unbalanced);

        // Its groups are not counted either: a pattern that is none has no groups to count.
        string counted = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "a)|(b", ["group"] = 2 });
        Assert.Contains("not a valid RE2", counted);

        string quoted = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "\\QStatus.Open", ["wholeWord"] = true });
        Assert.Contains("    3      2  Status.Open", quoted);
    }

    [Fact]
    public async Task A_malformed_pattern_is_explained()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string unbalanced = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "Status\\.(" });
        Assert.Contains("not a valid RE2", unbalanced);
        Assert.DoesNotContain("No matches", unbalanced);

        // A quoted \1 is two literal characters, not a backreference.
        string quoted = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "\\Q\\1\\E" });
        Assert.DoesNotContain("backreference (", quoted);
        Assert.StartsWith("No matches", quoted);

        string lookbehind =
            await ListAsync(client, new Dictionary<string, object?> { ["query"] = "(?<=Status\\.)(\\w+)" });
        Assert.Contains("lookbehind", lookbehind);

        string empty = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "   " });
        Assert.Contains("The pattern is empty", empty);

        string unknownRepository = await ListAsync(client,
            new Dictionary<string, object?> { ["query"] = "Status\\.(\\w+)", ["group"] = 1, ["repo"] = "three" });
        Assert.Contains("No repository 'three'", unknownRepository);
    }

    [Fact]
    public async Task The_limit_says_how_much_it_is_not_showing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await ListAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Include=\"([^\"]+)\"", ["group"] = 1, ["limit"] = 1 });

        Assert.Contains("2 distinct values", text);
        Assert.Contains("showing the 1 most frequent (raise limit for the rest)", text);
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        string text = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "Status\\.(\\w+)" });

        Assert.Contains("no index to read from", text);
        Assert.Contains("POST /api/projects/alpha/refresh", text);
    }

    private async Task<McpClient> StartAsync(SearchEngine engine)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync("alpha", TwoRepositories);
        return await _host.ConnectAsync("alpha");
    }

    private static Task<string> ListAsync(McpClient client, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, "list_matches", arguments);
}
