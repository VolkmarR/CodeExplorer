using System.Globalization;
using System.Text.Json;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using CodeExplorer.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The paged and capped reads answer the same under a culture that spells numbers its own way as
///     under the invariant one (#261). Swedish writes a minus as U+2212, which SQL does not read, so a
///     number formatted into statement text with the current culture is a syntax error there, or a
///     different number. Each read is asked through its service on the test's own thread, because the
///     in-process server does not carry the test's culture over, and asked for a page past the first
///     and a cap below the total, so the paging and capping numbers are in the statement.
///     A guard rather than a reproduction: no tool lets a caller make these numbers negative, and a
///     positive integer is spelled the same in every culture .NET ships, so the old statements passed
///     this too. What it holds is the bound form: a site that goes back to formatting a number, and
///     one day formats a negative one, fails here.
/// </summary>
public sealed class CultureTests
{
    private static async Task<TestHost> IndexedAsync(SearchEngine engine)
    {
        var host = new TestHost(engine);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/a/A.cs"] = "needle one\nhay\nneedle two\nneedle three\n",
                ["src/a/b/B.cs"] = "needle\n",
                ["src/C.cs"] = "needle\nneedle\n",
                ["docs/D.md"] = "needle\n"
            }
        });
        for (int i = 1; i <= 5; i++)
            host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { [$"src/E{i}.cs"] = "needle\n" },
                $"Commit {i}", i % 2 == 0 ? "Ada" : "Bob", i % 2 == 0 ? "ada@example.invalid" : "bob@example.invalid",
                i);
        await host.RefreshAsync("alpha");
        return host;
    }

    [Fact]
    public async Task Git_log_paging_and_the_authors_cap_answer_the_same_under_a_culture_with_its_own_minus_sign()
    {
        using var host = await IndexedAsync(SearchEngine.Substring);
        var history = host.Services.GetRequiredService<HistoryQueries>();

        var (invariantLog, swedishLog) = await UnderBothCulturesAsync(token =>
            history.LogAsync("alpha", new LogRequest(null, 2, 2), token));
        var (invariantAuthors, swedishAuthors) = await UnderBothCulturesAsync(token =>
            history.AuthorsAsync("alpha", new AuthorsRequest(null, 1), token));

        Assert.Contains("Commit 3", invariantLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Commit 5", invariantLog, StringComparison.Ordinal);
        Assert.Contains("example.invalid", invariantAuthors, StringComparison.Ordinal);
        Assert.Equal(invariantLog, swedishLog);
        Assert.Equal(invariantAuthors, swedishAuthors);
    }

    /// <summary>Over both engines, because they build the match differently and share the paging around it.</summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task Grep_paging_context_and_line_cap_answer_the_same_under_a_culture_with_its_own_minus_sign(
        SearchEngine engine)
    {
        using var host = await IndexedAsync(engine);
        var grep = host.Services.GetRequiredService<GrepSearch>();

        // With context and without it, because the two read the window through different statements.
        foreach (int context in new[] { 0, 1 })
        {
            var (invariant, swedish) = await UnderBothCulturesAsync(token =>
                grep.SearchAsync("alpha",
                    new GrepRequest("needle", Context: context, MaxLinesPerFile: 2, Page: 2, PageSize: 1), token));

            Assert.Contains("needle", invariant, StringComparison.Ordinal);
            Assert.Equal(invariant, swedish);
        }
    }

    [Fact]
    public async Task A_capped_match_list_answers_the_same_under_a_culture_with_its_own_minus_sign()
    {
        using var host = await IndexedAsync(SearchEngine.Substring);
        var matches = host.Services.GetRequiredService<MatchList>();

        var (invariant, swedish) = await UnderBothCulturesAsync(token =>
            matches.ListAsync("alpha",
                new MatchListRequest("needle (\\w+)", new FileFilter(null, null, null, null), Group: 1, Limit: 2),
                token));

        Assert.Contains("one", invariant, StringComparison.Ordinal);
        Assert.Equal(invariant, swedish);
    }

    [Fact]
    public async Task Glob_paging_and_tree_depth_answer_the_same_under_a_culture_with_its_own_minus_sign()
    {
        using var host = await IndexedAsync(SearchEngine.Substring);
        var files = host.Services.GetRequiredService<FileQueries>();

        var (invariantGlob, swedishGlob) = await UnderBothCulturesAsync(token =>
            files.GlobAsync("alpha", new GlobRequest("**/*.cs", null, 2, 2), token));
        var (invariantTree, swedishTree) = await UnderBothCulturesAsync(token =>
            files.TreeAsync("alpha", new TreeRequest("one", 3), token));

        Assert.Contains(".cs", invariantGlob, StringComparison.Ordinal);
        // src/a is two levels down, so its files are listed only where the depth reached the statement.
        Assert.Contains("A.cs", invariantTree, StringComparison.Ordinal);
        Assert.Equal(invariantGlob, swedishGlob);
        Assert.Equal(invariantTree, swedishTree);
    }

    /// <summary>
    ///     The same read under the invariant culture and then under Swedish, each answer serialised so
    ///     the two compare by value, lists included.
    /// </summary>
    private static async Task<(string Invariant, string Swedish)> UnderBothCulturesAsync(
        Func<CancellationToken, Task<Outcome>> read)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = Serialised(await read(TestContext.Current.CancellationToken));
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            string swedish = Serialised(await read(TestContext.Current.CancellationToken));
            return (invariant, swedish);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }

        static string Serialised(Outcome outcome) => JsonSerializer.Serialize(outcome, outcome.GetType());
    }
}
