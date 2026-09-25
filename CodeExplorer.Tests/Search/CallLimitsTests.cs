using System.Globalization;
using System.Text.RegularExpressions;
using CodeExplorer.Index;
using CodeExplorer.Reading;
using CodeExplorer.Search;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     One MCP call may not make the server do work the caller sizes without a bound
///     (GHSA-v284-9964-6mjr). Output caps trim a reply after the work is done, so each of these asks
///     for an absurd amount of work and expects a prompt answer that names the limit it hit.
///     The timeouts are the assertion that the work was not done: every case here ran for minutes, or
///     until the process ran out of memory, before the limits.
/// </summary>
public sealed class CallLimitsTests(CallLimitsFixture fixture) : IClassFixture<CallLimitsFixture>
{
    private readonly TestHost _host = fixture.Host;

    [Fact]
    public async Task List_tree_refuses_a_depth_past_the_limit_and_names_it()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);

        string reply = await CallAsync(client, "list_tree",
            new Dictionary<string, object?> { ["path"] = "one", ["depth"] = 1_000_000_000 });

        Assert.Contains($"depth may be at most {FileQueries.MaxTreeDepth}", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("src/", reply, StringComparison.Ordinal);

        string deepest = await CallAsync(client, "list_tree",
            new Dictionary<string, object?> { ["path"] = "one", ["depth"] = FileQueries.MaxTreeDepth });
        Assert.Contains("src/Orders.cs", deepest, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A star per recursion level: SQL GLOB backtracks at each one, so this pattern against a long
    ///     path it cannot match (no path holds a <c>~</c>) is polynomial in the path length with the
    ///     star count as the exponent. The glob tool and every path term (grep's path and exclude) are
    ///     matched in linear time instead.
    /// </summary>
    [Fact]
    public async Task A_backtracking_glob_answers_promptly_wherever_a_caller_can_pass_one()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);

        string glob = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = Backtracking });
        Assert.Contains($"No indexed file matches \"{Backtracking}\"", glob, StringComparison.Ordinal);

        string included = await CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Needle", ["regex"] = true, ["path"] = Backtracking });
        Assert.Contains("No matches", included, StringComparison.Ordinal);

        string excluded = await CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Needle", ["regex"] = true, ["exclude"] = Backtracking });
        Assert.Contains(CallLimitsFixture.LongPath, excluded, StringComparison.Ordinal);

    }

    /// <summary>
    ///     A path term is refused, as a sentence naming the argument, where it is malformed or larger
    ///     than a filter anyone writes: a reversed range (which GLOB matches nothing with, so it would
    ///     read as a search that found nothing), a term past the length limit, or too many terms.
    ///     The queries are regex so that the answer does not depend on the search engine.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedTerms))]
    public async Task A_malformed_or_oversized_path_term_is_refused_by_name(string argument, string term,
        string refusal)
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);

        string reply = await CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "Needle", ["regex"] = true, [argument] = term });

        Assert.Contains(refusal, reply, StringComparison.Ordinal);
        Assert.Contains($"`{argument}`", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("No matches", reply, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> MalformedTerms => new()
    {
        { "path", "*[z-a]*", "has the range [z-a], which runs backwards" },
        { "exclude", "*/[!z-a]rders.cs", "has the range [z-a], which runs backwards" },
        { "path", "*" + new string('a', GlobRegex.MaxLength), $"may be at most {GlobRegex.MaxLength} characters" },
        {
            "exclude", string.Join(",", Enumerable.Range(0, PathTerms.MaxTerms + 1).Select(i => $"/d{i}/")),
            $"takes at most {PathTerms.MaxTerms} comma-separated terms"
        }
    };

    [Fact]
    public async Task A_glob_past_the_length_limit_is_refused_by_name()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);

        string reply = await CallAsync(client, "glob",
            new Dictionary<string, object?> { ["glob"] = new string('?', GlobRegex.MaxLength + 1) });

        Assert.Contains($"A glob may be at most {GlobRegex.MaxLength} characters", reply, StringComparison.Ordinal);
    }

    /// <summary>What a GLOB means is kept by the linear matcher: classes, negation, `?` and case.</summary>
    [Theory]
    [InlineData("*Orders.cs", "one/src/Orders.cs")]
    [InlineData("ONE/SRC/*.CS", "one/src/Orders.cs")]
    [InlineData("one/src/Order?.cs", "one/src/Orders.cs")]
    [InlineData("*/[a-z]rders.cs", "one/src/Orders.cs")]
    [InlineData("*/[!x]rders.cs", "one/src/Orders.cs")]
    [InlineData("*read(me).md", "one/read(me).md")]
    [InlineData("*/[!O]rders.cs", null)]
    [InlineData("one/Orders.cs", null)]
    public async Task A_glob_matches_what_sql_glob_matched(string pattern, string? expected)
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);

        string reply = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = pattern });

        if (expected is null) Assert.Contains("No indexed file matches", reply, StringComparison.Ordinal);
        else Assert.Contains(expected, reply, StringComparison.Ordinal);
    }

    /// <summary>A range GLOB accepts and matches nothing with is refused, not answered with an empty list.</summary>
    [Fact]
    public async Task A_glob_with_a_reversed_range_is_refused_by_name()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);

        string reply = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*[z-a].cs" });

        Assert.Contains("[z-a], which runs backwards", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("No indexed file", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_file_refuses_more_entries_than_the_limit_and_names_it()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);
        string[] copies = Enumerable.Repeat("one/big.txt:1-60000", 1000).ToArray();

        string reply = await CallAsync(client, "read_file", new Dictionary<string, object?> { ["paths"] = copies });

        Assert.Contains($"at most {FileQueries.MaxWindows} entries, and this one has 1000", reply,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Explicit ranges skip the per-entry maxLines clamp, so the lines of one call are a budget the
    ///     entries share: the second window is cut where the budget ends, and the third is not read.
    ///     Both say so. Where the cut window continues is the reply cap's to say here, since a window this
    ///     long is cut by the reply first.
    /// </summary>
    [Fact]
    public async Task Read_file_shares_one_line_budget_between_its_entries()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);
        int rest = FileQueries.MaxLinesPerRead - CallLimitsFixture.BigLines;

        string reply = await CallAsync(client, "read_file", new Dictionary<string, object?>
        {
            ["paths"] = _overBudget
        });

        Assert.Contains($"one call reads at most {FileQueries.MaxLinesPerRead} lines", reply, StringComparison.Ordinal);
        Assert.Contains($"(lines 1-{rest} of {CallLimitsFixture.BigLines})", reply, StringComparison.Ordinal);
        Assert.Contains($"'one/README.md' was not read: the entries before it already read as much as one call reads",
            reply, StringComparison.Ordinal);
        Assert.DoesNotContain("readme", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Lines are not the only measure: a hundred entries over one minified file are a hundred lines
    ///     of megabytes each. Once the windows read reach the character budget the rest are not read.
    ///     Each file here is fifty lines of a hundred thousand characters, so four reach it and the fifth entry is not read.
    /// </summary>
    [Fact]
    public async Task Read_file_stops_at_its_character_budget_however_few_the_lines()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Large);

        string reply = await CallAsync(client, "read_file",
            new Dictionary<string, object?> { ["paths"] = _wideWindows });

        Assert.Contains("'large/b.txt' was not read", reply, StringComparison.Ordinal);
        Assert.Contains($"{FileQueries.MaxCharactersPerRead / 1_000_000} million characters", reply,
            StringComparison.Ordinal);
        Assert.Contains("large/b.txt  -", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One multiline match can span a whole file, so the lines it marks are capped too. Asked of the
    ///     search rather than the tool, because the reply cap would hide the difference.
    /// </summary>
    [Fact]
    public async Task A_multiline_match_spanning_the_file_marks_no_more_lines_than_the_cap()
    {
        var grep = _host.Services.GetRequiredService<GrepSearch>();

        var outcome = await grep.SearchAsync(CallLimitsFixture.Alpha,
            new GrepRequest("(?s)x.*", Path: "*big.txt", Multiline: true, Context: GrepSearch.MaxContext,
                MaxLinesPerFile: GrepSearch.MaxLinesPerFile), TestContext.Current.CancellationToken);

        var file = Assert.Single(Assert.IsType<GrepResult>(outcome).Files);
        Assert.Equal(GrepSearch.MaxMultilineLinesShown, file.Lines.Count);
        Assert.True(CallLimitsFixture.BigLines > GrepSearch.MaxMultilineLinesShown);
    }

    /// <summary>
    ///     A multiline page reads each file on it whole into the server's memory, so it is bounded by
    ///     bytes and not only by files: the first file is always read, and a file that would take the
    ///     page past the budget is listed with its count and not read, saying how to see it.
    /// </summary>
    [Fact]
    public async Task A_multiline_page_reads_no_more_file_content_than_its_byte_budget()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Large);

        string reply = await CallAsync(client, "grep", new Dictionary<string, object?>
        {
            ["query"] = @"Needle\s+Holder", ["multiline"] = true, ["pageSize"] = 3
        });

        Assert.Contains("3 files match in total", reply, StringComparison.Ordinal);
        // One file read and marked, two listed without their lines.
        Assert.Equal(1, Regex.Count(reply, @"^\s*1: Needle", RegexOptions.Multiline));
        Assert.Equal(2, Regex.Count(reply,
            $"lines not shown: a multiline page reads at most {GrepSearch.MaxMultilinePageMiB} MiB"));
        Assert.Contains("pageSize=1 and page=2", reply, StringComparison.Ordinal);
        Assert.Contains("pageSize=1 and page=3", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The limits are written into the tool descriptions by hand, because an attribute cannot
    ///     interpolate an int; this is what keeps the two from drifting apart.
    /// </summary>
    [Fact]
    public async Task The_tool_descriptions_state_the_limits_the_server_enforces()
    {
        await using var client = await _host.ConnectAsync(CallLimitsFixture.Alpha);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        // The description and the parameters' schema, where a parameter's own range is written.
        string Described(string name) =>
            tools.Single(t => t.Name == name) is var tool ? tool.Description + tool.JsonSchema : "";

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture,
                $"at most {FileQueries.MaxWindows} entries and reads at most {FileQueries.MaxLinesPerRead:N0} lines"),
            Described("read_file"), StringComparison.Ordinal);
        Assert.Contains($"{FileQueries.MaxCharactersPerRead / 1_000_000} million characters", Described("read_file"),
            StringComparison.Ordinal);
        Assert.Contains($"At most {PathTerms.MaxTerms} terms, each at most {GlobRegex.MaxLength} characters",
            Described("grep"), StringComparison.Ordinal);
        Assert.Contains($"1-{FileQueries.MaxTreeDepth}", Described("list_tree"), StringComparison.Ordinal);
        Assert.Contains($"at most {GrepSearch.MaxMultilinePageMiB} MiB of file content",
            Described("grep"), StringComparison.Ordinal);
    }

    private static readonly string[] _overBudget = ["one/big.txt:1-60000", "one/big.txt:1-60000", "one/README.md"];

    private static readonly string[] _wideWindows = ["large/a.txt", "large/b.txt", "large/c.txt", "large/a.txt", "large/b.txt:1-2"];

    private const string Backtracking = "*?*?*?*?*?*?*?*?*?*?*?*?~";

    /// <summary>
    ///     A tool call that must come back within <see cref="_prompt" />, which is the assertion that
    ///     the server did not do the work asked of it: each call here took minutes before its limit.
    /// </summary>
    private static Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, tool, arguments).WaitAsync(_prompt, TestContext.Current.CancellationToken);

    private static readonly TimeSpan _prompt = TimeSpan.FromSeconds(20);
}

/// <summary>A small project: what is under test is how much work a call may ask for, not the index.</summary>
public sealed class CallLimitsFixture : IAsyncLifetime
{
    public const string Alpha = "alpha";

    /// <summary>More than half of <see cref="FileQueries.MaxLinesPerRead" />, so two windows over it overrun the budget.</summary>
    public const int BigLines = 60_000;

    /// <summary>Long enough that a glob backtracking at each star is billions of steps against it.</summary>
    public const string LongPath = "one/src/deeply/nested/folder/with/a/long/name/NeedleHolder.cs";

    /// <summary>Three files each past half of <see cref="GrepSearch.MaxMultilinePageBytes" />, all matching one multiline pattern.</summary>
    public const string Large = "large";

    public TestHost Host { get; } = new(SearchEngine.Substring);

    public async ValueTask InitializeAsync()
    {
        // Long lines rather than many: what is measured is bytes, and a few rows index faster.
        int lines = (int)(GrepSearch.MaxMultilinePageBytes * 5 / 8 / 100_001);
        string large = "Needle\nHolder\n" + string.Concat(Enumerable.Repeat(new string('x', 100_000) + "\n", lines));
        await Host.IndexedProjectAsync(Large, new Dictionary<string, Dictionary<string, string>>
        {
            // Not `one`: a fixture repository belongs to the host, and Alpha has committed that one.
            ["large"] = new() { ["a.txt"] = large, ["b.txt"] = large, ["c.txt"] = large }
        });
        await Host.IndexedProjectAsync(Alpha, new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Orders.cs"] = "class Orders\n{\n    void Needle() {}\n}\n",
                ["README.md"] = "readme\n",
                ["read(me).md"] = "metacharacters in a name\n",
                ["big.txt"] = string.Concat(Enumerable.Repeat("x\n", BigLines)),
                [LongPath["one/".Length..]] = "class NeedleHolder {}\n"
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        return ValueTask.CompletedTask;
    }
}
