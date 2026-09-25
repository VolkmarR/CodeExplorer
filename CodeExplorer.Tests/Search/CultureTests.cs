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
/// </summary>
public sealed class CultureTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    private async Task FixtureAsync()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
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
            _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { [$"src/E{i}.cs"] = "needle\n" },
                $"Commit {i}", "Ada", "ada@example.invalid", i);
        await _host.RefreshAsync("alpha");
    }

    [Fact]
    public async Task Git_log_paging_answers_the_same_under_a_culture_with_its_own_minus_sign()
    {
        await FixtureAsync();
        var history = _host.Services.GetRequiredService<HistoryQueries>();

        var (invariant, swedish) = await UnderBothCulturesAsync(token =>
            history.LogAsync("alpha", new LogRequest(null, 2, 2), token));

        Assert.Contains("Commit 3", invariant, StringComparison.Ordinal);
        Assert.DoesNotContain("Commit 5", invariant, StringComparison.Ordinal);
        Assert.Equal(invariant, swedish);
    }

    [Fact]
    public async Task Grep_paging_context_and_line_cap_answer_the_same_under_a_culture_with_its_own_minus_sign()
    {
        await FixtureAsync();
        var grep = _host.Services.GetRequiredService<GrepSearch>();

        var (invariant, swedish) = await UnderBothCulturesAsync(token =>
            grep.SearchAsync("alpha",
                new GrepRequest("needle", Context: 1, MaxLinesPerFile: 2, Page: 2, PageSize: 1), token));

        Assert.Contains("needle", invariant, StringComparison.Ordinal);
        Assert.Equal(invariant, swedish);
    }

    [Fact]
    public async Task Glob_paging_and_tree_depth_answer_the_same_under_a_culture_with_its_own_minus_sign()
    {
        await FixtureAsync();
        var files = _host.Services.GetRequiredService<FileQueries>();

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
