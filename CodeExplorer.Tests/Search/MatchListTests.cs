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
        Assert.Contains("outside your repo/path/ext/exclude filters", hidden);

        string nowhere = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "Unicorn\\w+" });
        Assert.StartsWith("No matches", nowhere);
        Assert.DoesNotContain("outside your", nowhere);
        Assert.Contains("Try the same pattern with grep", nowhere);
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

    [Fact]
    public async Task A_malformed_pattern_is_explained()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string unbalanced = await ListAsync(client, new Dictionary<string, object?> { ["query"] = "Status\\.(" });
        Assert.Contains("not a valid RE2", unbalanced);
        Assert.DoesNotContain("No matches", unbalanced);

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
