using System.Net.Http.Json;
using CodeExplorer.Index;
using CodeExplorer.Operator;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The settings page's "Suggest from repositories" (#217): patterns proposed from the index by
///     three rules — what a <c>.gitattributes</c> declares, well-known generated names, and files the
///     history only ever bumps — each with the files it matches at HEAD, and none already in the
///     setting or matching nothing.
/// </summary>
public sealed class ExcludedPathSuggestionsTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task What_a_gitattributes_marks_generated_or_vendored_is_suggested_relative_to_its_folder()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                [".gitattributes"] = """
                                     # generated code
                                     Generated/** linguist-generated
                                     *.g.cs linguist-generated=true
                                     /vendor/** linguist-vendored
                                     docs/** -linguist-generated
                                     Nothing/** linguist-generated
                                     *.cs text eol=crlf
                                     """,
                ["Generated/A.cs"] = "a\n", ["Generated/B.cs"] = "b\n", ["src/X.g.cs"] = "x\n",
                ["vendor/lib.js"] = "v\n", ["docs/read.md"] = "d\n", ["src/Main.cs"] = "m\n"
            }
        });

        var suggestions = await SuggestAsync("alpha");

        // docs is marked NOT generated, Nothing matches no file, and eol says nothing about noise.
        Assert.Equal(
            [
                new ExcludedPathSuggestion("one/Generated/**", SuggestionRule.GitAttributes,
                    "linguist-generated in one/.gitattributes", 2),
                new ExcludedPathSuggestion("one/**/*.g.cs", SuggestionRule.GitAttributes,
                    "linguist-generated in one/.gitattributes", 1),
                new ExcludedPathSuggestion("one/vendor/**", SuggestionRule.GitAttributes,
                    "linguist-vendored in one/.gitattributes", 1)
            ],
            suggestions.Where(s => s.Rule == SuggestionRule.GitAttributes));
    }

    [Fact]
    public async Task A_well_known_generated_name_is_suggested_only_where_the_index_holds_one()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["Form1.Designer.cs"] = "d\n", ["Tests/A.verified.txt"] = "v\n", ["Tests/B.verified.txt"] = "w\n",
                ["web/package-lock.json"] = "{}\n", ["Properties/AssemblyInfo.cs"] = "i\n", ["Main.cs"] = "m\n",
                ["delphi/__history/Unit1.pas.~1~"] = "u\n", ["web/src/routeTree.gen.ts"] = "r\n",
                ["app/ios/Podfile.lock"] = "p\n"
            }
        });

        var suggestions = await SuggestAsync("alpha");

        // One of each ecosystem's names: .NET, Delphi, React, React Native.
        Assert.Equal(
            [
                ("**/*.Designer.cs", 1), ("**/AssemblyInfo.*", 1), ("**/*.verified.txt", 2),
                ("**/__history/**", 1), ("**/*.gen.ts", 1), ("**/package-lock.json", 1), ("**/Podfile.lock", 1)
            ],
            suggestions.Where(s => s.Rule == SuggestionRule.WellKnownName).Select(s => (s.Pattern, s.Files)));
    }

    [Fact]
    public async Task A_file_the_history_only_ever_bumps_is_suggested_and_a_busy_one_is_not()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["Version.txt"] = "v0\n", ["Busy.cs"] = Lines(20, "0"), ["Rare.txt"] = "r0\n" },
            ["two"] = new() { ["Version.txt"] = "v0\n" }
        });
        for (int i = 1; i <= ExcludedPathSuggestions.MinCommits; i++)
        {
            _host.CommitToGitRepositoryAs("one",
                new Dictionary<string, string> { ["Version.txt"] = $"v{i}\n", ["Busy.cs"] = Lines(20, $"{i}") },
                $"Bump {i}", "Ada", "ada@example.invalid", i);
            _host.CommitToGitRepositoryAs("two", new Dictionary<string, string> { ["Version.txt"] = $"v{i}\n" },
                $"Bump {i}", "Ada", "ada@example.invalid", i);
        }

        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["Rare.txt"] = "r1\n" }, "Rare", "Ada",
            "ada@example.invalid", 100);
        await _host.RefreshAsync("alpha");

        var history = Assert.Single(await SuggestAsync("alpha"), s => s.Rule == SuggestionRule.History);

        // Every Version.txt at HEAD is a bump, so the name is suggested rather than each path.
        Assert.Equal("**/Version.txt", history.Pattern);
        Assert.Equal(2, history.Files);
        Assert.Contains($"{ExcludedPathSuggestions.MinCommits + 1} commits", history.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Where_only_some_files_of_a_name_are_bumps_their_paths_are_suggested()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["Version.txt"] = "v0\n", ["Other/Version.txt"] = "o\n" }
        });
        for (int i = 1; i < ExcludedPathSuggestions.MinCommits; i++)
            _host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["Version.txt"] = $"v{i}\n" },
                $"Bump {i}", "Ada", "ada@example.invalid", i);
        await _host.RefreshAsync("alpha");

        var history = Assert.Single(await SuggestAsync("alpha"), s => s.Rule == SuggestionRule.History);

        Assert.Equal(("one/Version.txt", 1), (history.Pattern, history.Files));
    }

    [Fact]
    public async Task A_pattern_already_in_the_setting_or_covering_nothing_new_is_not_suggested()
    {
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["A.verified.txt"] = "v\n", ["Form1.Designer.cs"] = "d\n",
                [".gitattributes"] = "*.Designer.cs linguist-generated\n"
            }
        });
        await _host.SetExcludedPathsAsync("alpha", ["**/*.VERIFIED.txt"]);

        var suggestions = await SuggestAsync("alpha");

        // The gitattributes rule runs first and names the designer files, so the well-known name adds
        // nothing and is not offered a second time under another spelling.
        Assert.Equal(["one/**/*.Designer.cs"], suggestions.Select(s => s.Pattern));
    }

    [Fact]
    public async Task A_project_never_built_has_nothing_to_suggest_and_says_why()
    {
        await _host.CreateProjectAsync("alpha");

        var detail = await DetailAsync("alpha");

        Assert.Empty(detail.Suggestions);
        Assert.NotNull(detail.Unavailable);
    }

    /// <summary><paramref name="count" /> distinct lines, the prefix making one version differ from the next.</summary>
    private static string Lines(int count, string prefix) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix}line {i}\n"));

    private async Task<IReadOnlyList<ExcludedPathSuggestion>> SuggestAsync(string slug)
    {
        var detail = await DetailAsync(slug);
        Assert.Null(detail.Unavailable);
        return detail.Suggestions;
    }

    private async Task<ExcludedPathSuggestionsDetail> DetailAsync(string slug)
    {
        using var http = _host.CreateClient();
        var detail = await http.GetFromJsonAsync<ExcludedPathSuggestionsDetail>(
            $"/api/projects/{slug}/excluded-paths/suggestions", Ct);
        Assert.NotNull(detail);
        return detail;
    }
}
