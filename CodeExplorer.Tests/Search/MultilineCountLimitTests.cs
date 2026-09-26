using CodeExplorer.Index;
using CodeExplorer.Search;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A multiline grep counts its matches by reading every candidate file whole, so that pass is
///     bounded by <see cref="GrepSearch.MaxMultilineCountBytes" /> and not by the size of the index
///     (#297). Four files, each three tenths of the budget: the first three fit, the fourth does not,
///     and only the fourth holds <c>Only Here</c>. An answer the budget cut must say its count is a
///     lower bound, and a miss it cut must never read as a clean negative.
/// </summary>
public sealed class MultilineCountLimitTests
{
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task The_counting_pass_stops_at_its_byte_budget_and_says_the_count_is_a_lower_bound(
        SearchEngine engine)
    {
        using var host = new TestHost(engine);
        int lines = (int)(GrepSearch.MaxMultilineCountBytes * 3 / 10 / 100_001);
        string filler = string.Concat(Enumerable.Repeat(new string('x', 100_000) + "\n", lines));
        string common = "Needle\nHolder\n" + filler;
        await host.IndexedProjectAsync("big", new Dictionary<string, Dictionary<string, string>>
        {
            ["big"] = new()
            {
                ["a.txt"] = common, ["b.txt"] = common, ["c.txt"] = common,
                ["d.txt"] = "Needle\nHolder\nOnly\nHere\n" + filler
            }
        });
        await using var client = await host.ConnectAsync("big");

        string cut = await CallAsync(client, new Dictionary<string, object?>
        {
            ["query"] = @"Needle\s+Holder", ["multiline"] = true, ["filesOnly"] = true
        });
        Assert.Contains("At least 3 files match (at least 3 matching lines)", cut, StringComparison.Ordinal);
        Assert.Contains(
            $"The count stopped after {GrepSearch.MaxMultilineCountMiB} MiB of candidate files, so 1 more file was not searched",
            cut, StringComparison.Ordinal);
        Assert.DoesNotContain("in total", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("big/d.txt", cut, StringComparison.Ordinal);

        // Only the file past the budget holds a match: a miss that is no proof of absence.
        string missed = await CallAsync(client, new Dictionary<string, object?>
        {
            ["query"] = Unnarrowed, ["multiline"] = true
        });
        Assert.Contains("No matches in the files searched", missed, StringComparison.Ordinal);
        Assert.Contains("1 more file was not searched", missed, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing matches", missed, StringComparison.Ordinal);

        // Within its filters the search is whole; without them, the check for matches outside is cut.
        string filtered = await CallAsync(client, new Dictionary<string, object?>
        {
            ["query"] = Unnarrowed, ["multiline"] = true, ["path"] = "big/a.txt"
        });
        Assert.Contains("Nothing matches within your filters", filtered, StringComparison.Ordinal);
        Assert.Contains("whether it matches outside them is not known", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("with or without your filters", filtered, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>Only Here</c> spelled without a literal the prefilter could narrow the candidates by: with
    ///     one, only the fourth file would be a candidate and the budget would never be reached.
    /// </summary>
    private const string Unnarrowed = @"[O][n][l][y]\s+[H][e][r][e]";

    private static Task<string> CallAsync(ModelContextProtocol.Client.McpClient client,
        Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, "grep", arguments);
}
