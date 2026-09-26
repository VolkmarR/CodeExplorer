using CodeExplorer.Index;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The <c>grep</c> tool over a project index (#5). Every test pins the engine; the text-mode tests
///     run under both, because <c>INSTALL fts</c> fails offline and an unpinned suite would exercise
///     full-text on a laptop and substring scan on CI.
/// </summary>
public sealed class GrepTests : IDisposable
{
    private static readonly Dictionary<string, Dictionary<string, string>> TwoRepositories = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] = "class Orders\n{\n    void Needle() {}\n    // needle in a comment\n}\n",
            ["src/Orders.g.cs"] = "// generated\nvoid Needle() {}\n",
            ["README.md"] = "first repository\n"
        },
        ["two"] = new Dictionary<string, string>
        {
            ["lib/index.ts"] = "export function needle() {}\nexport const haystack = 1;\n",
            ["lib/wrapped.ts"] =
                "repo.Update(entity,\n  e => e.Status = Done);\nrepo.Update(other, e => e.Status = Open);\n",
            ["lib/anchors.ts"] = "const myStatus = 1;\nStatus();\n"
        }
    };

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Theory]
    [InlineData(SearchEngine.Fts, Search.GrepSearch.TokenEngine)]
    [InlineData(SearchEngine.Substring, Search.GrepSearch.SubstringEngine)]
    public async Task Text_query_matches_in_both_repositories_and_names_the_engine(SearchEngine engine, string reported)
    {
        await using var client = await StartAsync(engine);

        string text = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "needle" });

        Assert.Contains($"({reported} engine)", text);
        Assert.Contains("one/src/Orders.cs", text);
        Assert.Contains("one/src/Orders.g.cs", text);
        Assert.Contains("two/lib/index.ts", text);
        // Matching lines are numbered with the grep ':' marker and the file's match count is stated.
        Assert.Contains("one/src/Orders.cs  -  2 matches", text);
        Assert.Contains("3:     void Needle() {}", text);
        Assert.Contains("4:     // needle in a comment", text);
    }

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task Regex_query_works_and_a_malformed_pattern_is_explained(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string text = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Need(le|les)\\(", ["regex"] = true, ["caseSensitive"] = true });
        Assert.Contains($"({Search.GrepSearch.RegexEngine} engine)", text);
        Assert.Contains("one/src/Orders.cs", text);
        Assert.DoesNotContain("two/lib/index.ts", text);

        string malformed = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "Needle(", ["regex"] = true });
        Assert.Contains("not a valid RE2", malformed);
        Assert.DoesNotContain("No matches", malformed);

        string lookbehind = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "(?<=void )Needle", ["regex"] = true });
        Assert.Contains("lookbehind", lookbehind);
        Assert.Contains("RE2", lookbehind);

        string backreference = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "(e)\\1", ["regex"] = true });
        Assert.Contains("backreference", backreference);
    }

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task No_matches_is_told_apart_from_matches_hidden_by_filters(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string hidden =
            await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "needle", ["ext"] = "md" });
        Assert.StartsWith("No matches", hidden);
        Assert.Contains("does match in 3 files outside your filters", hidden);

        string excluded = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "haystack", ["exclude"] = "*.ts" });
        Assert.Contains("does match in 1 file outside", excluded);

        string nowhere = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "unicorn", ["path"] = "one/" });
        Assert.Contains("Nothing matches anywhere in the project, with or without your filters", nowhere);

        string unfiltered = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "unicorn" });
        Assert.StartsWith("No matches", unfiltered);
        Assert.DoesNotContain("outside your", unfiltered);
        Assert.Contains("no filters narrowed the search, which spanned every file.", unfiltered);
    }

    /// <summary>
    ///     A token-path miss whose filters hid whole-token matches is not yet a verdict on the filters:
    ///     a longer name inside them — <c>MsgErrorDB</c> under the query <c>MsgError</c> — is something
    ///     the token path never counts, and a reply that only said "widen your filters" sent agents
    ///     away from the files they had scoped to. The substring path counts the longer name, so it
    ///     answers with it and there is no miss to word.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_filtered_token_miss_does_not_rule_out_a_longer_name_inside_the_filters(SearchEngine engine)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync("beta", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Mod770/Report.prg"] = "MsgErrorDB(self, oSql)\n",
                ["src/Other/Shared.prg"] = "MsgError(\"failed\")\n"
            }
        });
        await using var client = await _host.ConnectAsync("beta");

        string text = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "MsgError", ["path"] = "Mod770" });

        if (engine == SearchEngine.Substring)
        {
            Assert.Contains("one/src/Mod770/Report.prg", text);
            return;
        }

        Assert.StartsWith("No matches", text);
        Assert.Contains("does match in 1 file outside your filters", text);
        Assert.Contains("regex=true", text);
    }

    /// <summary>
    ///     The miss that knows the most: nothing hid a file from it, so the reply says what it searched
    ///     before falling back to any engine hint (#87). A hint survives only where it names a mechanism
    ///     that sentence does not.
    /// </summary>
    [Fact]
    public async Task An_unfiltered_miss_says_it_searched_the_whole_project()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string regex = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "EKLfsBewKto", ["regex"] = true });
        Assert.Contains(
            "Nothing matches anywhere in the project; no filters narrowed the search, which spanned every file.",
            regex);
        Assert.DoesNotContain("Try a looser pattern", regex);
        Assert.Contains("try a shorter fragment", regex);

        // wholeWord narrows a search without filtering a file out of it, so the scope sentence and the
        // hint that names it must not contradict each other.
        string wholeWord = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "EKLfsBewKto", ["regex"] = true, ["wholeWord"] = true });
        Assert.Contains("no filters narrowed the search, which spanned every file.", wholeWord);
        Assert.Contains("wholeWord=true", wholeWord);

        string text = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "EKLfsBewKto" });
        Assert.Contains("no filters narrowed the search, which spanned every file.", text);
        Assert.Contains("Retry with regex=true", text);

        // A filtered miss is a different fact and keeps its own sentence: the filters were part of it.
        string filtered = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "EKLfsBewKto", ["ext"] = "cs" });
        Assert.Contains("Nothing matches anywhere in the project, with or without your filters", filtered);
        Assert.DoesNotContain("no filters narrowed the search", filtered);
    }

    [Fact]
    public async Task Filters_context_files_only_and_paging_shape_the_answer()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string excluded = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "Needle", ["exclude"] = "*.g.cs", ["caseSensitive"] = true });
        Assert.Contains("one/src/Orders.cs", excluded);
        Assert.DoesNotContain("Orders.g.cs", excluded);
        Assert.DoesNotContain("index.ts", excluded);

        string context = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "Needle", ["path"] = "one/src/Orders.cs", ["context"] = 1 });
        Assert.Contains("2- {", context);
        Assert.Contains("3:     void Needle() {}", context);
        Assert.Contains("4:     // needle in a comment", context);
        Assert.Contains("5- }", context);

        string filesOnly = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "needle", ["filesOnly"] = true });
        Assert.Contains("3 files match in total (4 matching lines)", filesOnly);
        Assert.Contains("     2  one/src/Orders.cs", filesOnly);
        Assert.DoesNotContain("Needle() {}", filesOnly);

        string firstPage = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "needle", ["pageSize"] = 2 });
        Assert.Contains("page 1 of 2", firstPage);
        Assert.Contains("Call grep again with page=2", firstPage);
        string secondPage = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "needle", ["pageSize"] = 2, ["page"] = 2 });
        Assert.Contains("page 2 of 2", secondPage);
        Assert.DoesNotContain("Call grep again", secondPage);

        string capped = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "needle", ["maxLinesPerFile"] = 1 });
        Assert.Contains("1 more match in this file", capped);
    }

    [Fact]
    public async Task Multiline_matches_span_lines_and_every_spanned_line_is_marked()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "Update\\([^)]*Status\\s*=", ["multiline"] = true });

        Assert.Contains($"({Search.GrepSearch.MultilineEngine} engine)", text);
        Assert.Contains("two/lib/wrapped.ts  -  2 matches", text);
        Assert.Contains("1: repo.Update(entity,", text);
        Assert.Contains("2:   e => e.Status = Done);", text);
        Assert.Contains("3: repo.Update(other, e => e.Status = Open);", text);

        string hidden = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Update\\([^)]*Status", ["multiline"] = true, ["ext"] = "cs" });
        Assert.Contains("does match in 1 file outside", hidden);

        // Boundaries come from RE2 itself: the earlier "myStatus" on line 1 must not be marked.
        string anchored = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "\\bStatus\\(", ["multiline"] = true, ["caseSensitive"] = true });
        Assert.Contains("two/lib/anchors.ts  -  1 match", anchored);
        Assert.Contains("2: Status();", anchored);
        Assert.DoesNotContain("1: const myStatus", anchored);

        // One two-line match shown, one hidden: the footer counts matches, not marked lines.
        string capped = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Update\\([^)]*Status\\s*=", ["multiline"] = true, ["maxLinesPerFile"] = 1 });
        Assert.Contains("1: repo.Update(entity,", capped);
        Assert.Contains("2:   e => e.Status = Done);", capped);
        Assert.DoesNotContain("3: repo.Update(other", capped);
        Assert.Contains("1 more match in this file", capped);
    }

    /// <summary>
    ///     A pattern that can match nothing counts every empty match, as RE2's extract does, yet marks
    ///     only the lines a real match spans; and a context window stops at the first and last line.
    /// </summary>
    [Fact]
    public async Task Multiline_empty_matches_count_but_mark_nothing_and_context_stops_at_the_file_edges()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string emptyToo = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "(?:Done)?", ["multiline"] = true, ["path"] = "two/lib/wrapped.ts" });
        Assert.Contains("two/lib/wrapped.ts  -  84 matches", emptyToo);
        Assert.Contains("2:   e => e.Status = Done);", emptyToo);
        Assert.DoesNotContain("1: repo.Update(entity,", emptyToo);
        Assert.DoesNotContain("3: repo.Update(other", emptyToo);
        Assert.Contains("83 more matches in this file", emptyToo);

        string spanning = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "Done\\);\\nrepo", ["multiline"] = true, ["context"] = 2 });
        Assert.Contains("two/lib/wrapped.ts  -  1 match", spanning);
        Assert.Contains("1- repo.Update(entity,", spanning);
        Assert.Contains("2:   e => e.Status = Done);", spanning);
        Assert.Contains("3: repo.Update(other, e => e.Status = Open);", spanning);
        Assert.DoesNotContain("4-", spanning);
    }

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_page_past_the_end_still_reports_the_totals(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string text = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "needle", ["page"] = 7 });

        Assert.Contains("3 files match in total", text);
        Assert.Contains("Page 7 is past the end; the last page is 1.", text);
        Assert.DoesNotContain("No matches", text);
    }

    /// <summary>
    ///     The literal prefilter must never drop a file that matches (#234). Each pattern once fed the
    ///     filter a literal the match does not contain — the digits of <c>\x41</c>, the class name of
    ///     <c>\pL</c>, the <c>]</c> ending a <c>[:digit:]</c>, a newline no single line can hold — and
    ///     the unfiltered recount went through the same filter, so the miss looked like a real one.
    ///     One engine is enough: the multiline scan never reads the full-text index.
    /// </summary>
    [Theory]
    [InlineData("\\x41BC")]
    [InlineData("\\pLfoo")]
    [InlineData("[[:digit:]]x")]
    [InlineData("foo,\n  bar")]
    [InlineData("foo,\\\n  bar")]
    public async Task Multiline_prefilter_keeps_every_file_that_matches(string query)
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("prefilter", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/Fixture.cs"] = "ABC\nxfoo\n7x\nfoo,\n  bar\n" }
        });
        await using var client = await _host.ConnectAsync("prefilter");

        string text = await GrepAsync(client, new Dictionary<string, object?>
            { ["query"] = query, ["multiline"] = true, ["caseSensitive"] = true });

        Assert.Contains("one/src/Fixture.cs  -  1 match", text);
    }

    /// <summary>
    ///     The largest page a caller can spell is still just a page past the end (#233). Its offset,
    ///     page times page size, does not fit an <c>int</c>: wrapped, it went negative, which DuckDB
    ///     refused as an error and the multiline scan read as page 1 labelled page 2147483647.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts, false)]
    [InlineData(SearchEngine.Substring, false)]
    [InlineData(SearchEngine.Substring, true)]
    public async Task The_largest_page_is_a_page_past_the_end_and_not_an_error(SearchEngine engine, bool multiline)
    {
        await using var client = await StartAsync(engine);

        string text = await GrepAsync(client, new Dictionary<string, object?>
            { ["query"] = "needle", ["multiline"] = multiline, ["page"] = int.MaxValue, ["pageSize"] = 100 });

        Assert.Contains("3 files match in total", text);
        Assert.Contains($"Page {int.MaxValue} is past the end; the last page is 1.", text);
        Assert.DoesNotContain("one/src/Orders.cs", text);
    }

    /// <summary>
    ///     RE2 folds <c>s</c>, <c>S</c> and <c>ſ</c> (U+017F) together, but DuckDB's <c>lower('ſ')</c> is
    ///     still <c>ſ</c>, so a lower-cased prefilter dropped a file matching only through the long s
    ///     (#266). The case-insensitive flag and an inline <c>(?i)</c> both have to admit it. The Kelvin
    ///     sign needs nothing: <c>lower()</c> maps it to <c>k</c>, and the test pins that it stays found.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts, "(?i)class\\s+W", true, "one/src/Long.cs")]
    [InlineData(SearchEngine.Substring, "(?i)class\\s+W", true, "one/src/Long.cs")]
    [InlineData(SearchEngine.Fts, "class\\s+W", false, "one/src/Long.cs")]
    [InlineData(SearchEngine.Substring, "class\\s+W", false, "one/src/Long.cs")]
    [InlineData(SearchEngine.Fts, "(?i)claſs\\s+W", true, "one/src/Plain.cs")]
    [InlineData(SearchEngine.Substring, "(?i)claſs\\s+W", true, "one/src/Plain.cs")]
    [InlineData(SearchEngine.Fts, "(?i)kind\\s+W", true, "one/src/Kelvin.cs")]
    [InlineData(SearchEngine.Substring, "(?i)kind\\s+W", true, "one/src/Kelvin.cs")]
    public async Task Multiline_case_insensitive_prefilter_admits_what_re2_folds(SearchEngine engine, string query,
        bool caseSensitive, string expected)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync("fold", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Long.cs"] = "claſs Widget\n",
                ["src/Plain.cs"] = "class Widget\n",
                ["src/Kelvin.cs"] = "Kind Widget\n"
            }
        });
        await using var client = await _host.ConnectAsync("fold");

        string text = await GrepAsync(client, new Dictionary<string, object?>
            { ["query"] = query, ["multiline"] = true, ["caseSensitive"] = caseSensitive });

        Assert.Contains($"{expected}  -  1 match", text);
    }

    [Theory]
    [InlineData("(?i)class\\s+W", false, true)]
    [InlineData("class\\s+W", false, true)]
    [InlineData("class\\s+W", true, false)]
    [InlineData("(?i)claſs\\s+W", true, true)]
    [InlineData("(?i)kind\\s+W", false, false)]
    public void The_multiline_prefilter_widens_for_the_long_s_only_when_case_folds(string query, bool caseSensitive,
        bool widened)
    {
        var parameters = new List<DuckDB.NET.Data.DuckDBParameter>();

        string sql = Search.GrepSearch.LiteralPrefilter(query, caseSensitive, parameters);

        Assert.Contains("contains(", sql);
        Assert.Equal(widened, sql.Contains("replace("));
        Assert.DoesNotContain("ſ", (string)parameters.Single().Value!);
    }

    [Theory]
    [InlineData("Update\\([^)]*Status\\s*=", "Update(")]
    [InlineData("abc|def", null)]
    [InlineData("(abc|def)ghi", "ghi")]
    [InlineData("ab?cdef", "cdef")]
    [InlineData("ab*c", "a")]
    [InlineData("ab+c", "ab")]
    [InlineData("a.b", "a")]
    [InlineData("foo\\.bar", "foo.bar")]
    [InlineData("\\d+", null)]
    [InlineData("[abc]x{2}", null)]
    [InlineData("needle", "needle")]
    [InlineData("\\x41BC", "BC")]
    [InlineData("\\x{41}BC", "BC")]
    [InlineData("\\pLfoo", "foo")]
    [InlineData("\\p{Greek}foo", "foo")]
    [InlineData("\\101BC", "BC")]
    [InlineData("\\Qa.b\\Ecd", "cd")]
    [InlineData("[[:digit:]]x", "x")]
    [InlineData("[^]a]bc", "bc")]
    [InlineData("[\\]x]yz", "yz")]
    [InlineData("foo,\n  bar", "  bar")]
    [InlineData("foo,\\\n  bar", "  bar")]
    [InlineData("ab😀?", "ab")]
    [InlineData("[abc", null)]
    public void Required_literal_is_sound(string pattern, string? expected) =>
        Assert.Equal(expected, Search.GrepSearch.RequiredLiteral(pattern));

    /// <summary>The tests are ANDed, so a repeated word would only make the scan test it twice.</summary>
    [Theory]
    [InlineData(true, false, 1, 1)]
    [InlineData(true, true, 1, 2)]
    [InlineData(false, false, 0, 1)]
    [InlineData(false, true, 0, 2)]
    public void A_repeated_query_word_adds_no_duplicate_predicate(bool useTokens, bool caseSensitive, int pieces,
        int contains)
    {
        var parameters = new List<DuckDB.NET.Data.DuckDBParameter>();

        string sql = Search.GrepSearch.TextMatch(["needle", "Needle", "needle"], caseSensitive, useTokens, parameters);

        Assert.Equal(pieces, CountOf(sql, "regexp_matches("));
        Assert.Equal(contains, CountOf(sql, "contains("));
        Assert.Equal(pieces + contains, parameters.Count);

        static int CountOf(string text, string part) => text.Split(part).Length - 1;
    }

    /// <summary>Dropping the repeats must not change what the query finds, on either engine.</summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_repeated_query_word_finds_what_the_word_alone_finds(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string once = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "needle" });
        string repeated = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "needle Needle needle" });

        Assert.Equal(once.Replace("\"needle\"", ""), repeated.Replace("\"needle Needle needle\"", ""));
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        string text = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "needle" });

        Assert.Contains("no index to read from", text);
        Assert.Contains("POST /api/projects/alpha/refresh", text);
    }

    /// <summary>
    ///     The token rule the tokenised path applies, which used to be BM25's and is now a word-boundary
    ///     test. These are the cases that told the two engines apart before and still have to: a whole
    ///     token matches, the same letters inside a longer identifier do not, and every piece of a
    ///     multi-word query has to be present rather than only the first.
    /// </summary>
    [Fact]
    public async Task A_text_query_matches_whole_identifier_tokens_and_not_parts_of_longer_ones()
    {
        await using var client = await StartAsync(SearchEngine.Fts);

        string whole = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "Status" });
        Assert.Contains($"({Search.GrepSearch.TokenEngine} engine)", whole);
        // `Status();` and `e.Status` are the token; `myStatus` is not, and is the case a substring
        // scan would answer differently.
        Assert.Contains("lib/anchors.ts", whole);
        Assert.DoesNotContain("myStatus = 1", whole);

        // Conjunctive: both pieces must be on the line, which is why the query is split rather than
        // wrapped in one boundary test.
        string both = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "const haystack" });
        Assert.Contains("lib/index.ts", both);
        Assert.DoesNotContain("anchors.ts", both);
    }

    /// <summary>
    ///     A query carrying punctuation used to return nothing at all: the whole string went to BM25,
    ///     which found no such token, and a real answer was reported as a clean miss (#81). The pieces
    ///     are now split out of it the way the tokeniser splits them, so the punctuation narrows the
    ///     answer through contains() instead of erasing it.
    /// </summary>
    [Fact]
    public async Task A_query_with_punctuation_finds_the_lines_that_contain_it()
    {
        await using var client = await StartAsync(SearchEngine.Fts);

        string plain = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "Needle" });
        Assert.Contains("src/Orders.cs", plain);

        string called = await GrepAsync(client, new Dictionary<string, object?> { ["query"] = "Needle()" });
        Assert.Contains("src/Orders.cs", called);
        Assert.Contains("void Needle() {}", called);
        // The comment says "needle in a comment" with no parenthesis, so the punctuation is doing
        // work rather than being ignored.
        Assert.DoesNotContain("needle in a comment", called);
    }

    /// <summary>
    ///     wholeWord against letters outside ASCII (#235). RE2's <c>\b</c> is ASCII-only, so it put a
    ///     boundary inside <c>fooÄbar</c> and none in front of <c>Ändern</c>. The boundary that replaced
    ///     it consumes a character, and a match must not lose the one it shares with its neighbour —
    ///     across a comma or across a line break.
    /// </summary>
    [Fact]
    public async Task Whole_words_are_bounded_by_letters_outside_ascii_too()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("umlaut", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Glued.cs"] = "var x = fooÄbar;\n",
                ["src/Words.cs"] = "Ändern(bar,bar);\nbar\n"
            }
        });
        await using var client = await _host.ConnectAsync("umlaut");

        string bar = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "bar", ["regex"] = true, ["wholeWord"] = true });
        Assert.Contains("src/Words.cs", bar);
        Assert.DoesNotContain("src/Glued.cs", bar);

        string change = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "Ändern", ["regex"] = true, ["wholeWord"] = true });
        Assert.Contains("1: Ändern(bar,bar);", change);

        string spanned = await GrepAsync(client,
            new Dictionary<string, object?> { ["query"] = "bar", ["multiline"] = true, ["wholeWord"] = true });
        Assert.Contains("src/Words.cs  -  3 matches", spanned);
        Assert.Contains("1: Ändern(bar,bar);", spanned);
        Assert.Contains("2: bar", spanned);
        Assert.DoesNotContain("src/Glued.cs", spanned);
    }

    /// <summary>
    ///     The caller's pattern is wrapped for a whole word or for marking a multiline match, and a
    ///     wrapper must not change what it means (#238): <c>a)|(b</c> is not a pattern, though its
    ///     whole-word wrapping balances it into one, and a <c>\Q</c> left open would quote the marking's
    ///     own closing parenthesis.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task A_wrapped_pattern_means_what_it_meant_alone(bool multiline, bool wholeWord)
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string unbalanced = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "a)|(b", ["regex"] = true, ["multiline"] = multiline, ["wholeWord"] = wholeWord });
        Assert.Contains("not a valid RE2", unbalanced);

        string quoted = await GrepAsync(client,
            new Dictionary<string, object?>
                { ["query"] = "\\QStatus", ["regex"] = true, ["multiline"] = multiline, ["wholeWord"] = wholeWord });
        Assert.DoesNotContain("not a valid RE2", quoted);
        Assert.Contains("Status", quoted);
    }

    private async Task<McpClient> StartAsync(SearchEngine engine)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync("alpha", TwoRepositories);
        return await _host.ConnectAsync("alpha");
    }

    private static Task<string> GrepAsync(McpClient client, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, "grep", arguments);
}
