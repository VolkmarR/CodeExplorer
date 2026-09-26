using CodeExplorer.Index;
using CodeExplorer.Language;
using CodeExplorer.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     One word boundary for every tool that matches a whole word (#294). RE2 has no lookbehind and an
///     ASCII-only <c>\b</c> (#235), and <c>find_references</c> and whole-word <c>grep</c> and
///     <c>list_matches</c> each once worked around that their own way, so one line could be a match to
///     one tool and not to another. The same lines go through every one of them here, asked through
///     their services so the counts are read off the answer rather than out of reply prose.
/// </summary>
public sealed class WholeWordTests
{
    /// <summary>
    ///     Each symbol at the start and the end of a line, beside punctuation and one comma apart, and
    ///     glued to a letter outside ASCII, an underscore or a digit, where it is not a whole word.
    /// </summary>
    private const string Lines = """
                                 Ship(Ship(Ship_), Ships, xShip, Ship2);
                                 Änderung = Maß,Maß(Änderung);
                                 var ÜberMaß = xÄnderung + Maßband + Änderungen + _Maß + Maß1;
                                 _id = x_id + _id2 + 1_id + _id;
                                 v2 = av2 + v2_ + v2;
                                 return Maß
                                 Ship
                                 x = Log2.Log2.Log;

                                 """;

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task Every_whole_word_search_finds_the_same_lines_and_occurrences(SearchEngine engine)
    {
        using var host = new TestHost(engine);
        await host.IndexedProjectAsync("words", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/Words.cs"] = Lines }
        });
        var references = host.Services.GetRequiredService<ReferenceSearch>();
        var grep = host.Services.GetRequiredService<GrepSearch>();
        var matches = host.Services.GetRequiredService<MatchList>();
        var token = TestContext.Current.CancellationToken;
        var everything = new FileFilter();

        foreach ((string symbol, int lines, int occurrences) in new[]
                 {
                     ("Ship", 2, 3), ("Änderung", 1, 2), ("Maß", 2, 3), ("_id", 1, 2), ("v2", 1, 2),
                     // Starting again inside its own failed match: Log2.Log is not whole before the
                     // 2, and the whole one begins at the second Log. A count that resumed after the
                     // failed candidate lost it, on a line it had just matched.
                     ("Log2.Log", 1, 1)
                 })
        {
            string pattern = SymbolText.Re2Literal(symbol);

            var found = Assert.IsType<ReferenceResult>(
                await references.FindAsync("words", new ReferenceRequest(symbol, everything), token));
            var grepped = Assert.IsType<GrepResult>(await grep.SearchAsync("words",
                new GrepRequest(pattern, Regex: true, CaseSensitive: true, WholeWord: true), token));
            var spanned = Assert.IsType<GrepResult>(await grep.SearchAsync("words",
                new GrepRequest(pattern, CaseSensitive: true, Multiline: true, WholeWord: true), token));
            var listed = Assert.IsType<MatchListResult>(await matches.ListAsync("words",
                new MatchListRequest(pattern, everything, CaseSensitive: true, WholeWord: true), token));

            Assert.True(lines == found.TotalLines, $"find_references lines of {symbol}: {found.TotalLines}");
            Assert.True(lines == grepped.TotalLines, $"grep lines of {symbol}: {grepped.TotalLines}");
            Assert.True(occurrences == found.TotalOccurrences,
                $"find_references occurrences of {symbol}: {found.TotalOccurrences}");
            Assert.True(occurrences == found.References.Count,
                $"find_references classified occurrences of {symbol}: {found.References.Count}");
            // A multiline grep counts matches rather than lines.
            Assert.True(occurrences == spanned.TotalLines, $"multiline grep matches of {symbol}: {spanned.TotalLines}");
            Assert.True(occurrences == listed.TotalMatches, $"list_matches matches of {symbol}: {listed.TotalMatches}");
            // Equal counts could still be different lines, so the lines themselves are compared too.
            Assert.Equal(grepped.Files.SelectMany(f => f.Lines).Where(l => l.IsMatch).Select(l => l.LineNumber).Order(),
                found.References.Select(r => r.LineNumber).Distinct().Order());
        }
    }

    /// <summary>
    ///     A symbol that starts with punctuation is bounded at its word end only, as its re-finding on the
    ///     line is: a boundary against <c>$</c> would mean the opposite of what it means against a letter.
    ///     Here <c>find_references</c> parts from <c>grep -w</c>, which bounds whatever it is given.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_symbol_starting_with_punctuation_is_bounded_at_its_word_end_alone(SearchEngine engine)
    {
        using var host = new TestHost(engine);
        await host.IndexedProjectAsync("words", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/Words.js"] = "a$ship + $ship2 + $ship;\n" }
        });
        var references = host.Services.GetRequiredService<ReferenceSearch>();

        var found = Assert.IsType<ReferenceResult>(await references.FindAsync("words",
            new ReferenceRequest("$ship", new FileFilter()), TestContext.Current.CancellationToken));

        Assert.Equal(1, found.TotalLines);
        Assert.Equal(2, found.TotalOccurrences);
        Assert.Equal(2, found.References.Count);
    }
}
