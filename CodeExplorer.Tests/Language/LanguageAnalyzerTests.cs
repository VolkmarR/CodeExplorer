using CodeExplorer.Language;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The language seam (ADR-0008, #57). These are unit tests and not integration tests, which is the
///     exception CODING_STANDARDS allows for: there is no SQL in any of this, and the thing under test
///     is a decision about one line of text. What the seam changes for a real index is covered
///     end-to-end in <see cref="ReferenceTests" />.
/// </summary>
public sealed class LanguageAnalyzerTests
{
    /// <summary>What a language calls the appearance of <paramref name="symbol" /> on this line.</summary>
    private static ReferenceKind Kind(string extension, string line, string symbol) =>
        Kind(Languages.Default.For(extension), line, symbol);

    /// <summary>
    ///     The first appearance of the symbol on the line, as this analyser places it. The line is read
    ///     as the first line of a file, which is what a one-line test means.
    /// </summary>
    private static ReferenceKind Kind(ILanguageAnalyzer analyzer, string line, string symbol)
    {
        var placed = analyzer.Occurrences(analyzer.Start, line, symbol);
        Assert.NotEmpty(placed);
        return placed[0].Value;
    }

    /// <summary>
    ///     What this line declares, read as the first line of a file — which is what a one-line test
    ///     means, and which leaves the declaration/implementation split unsaid.
    /// </summary>
    private static Answer<Declared?> Declares(string extension, string line)
    {
        var analyzer = Languages.Default.For(extension);
        return analyzer.Declares(analyzer.Start, line);
    }

    /// <summary>
    ///     What each line of this file declares, each read from what the lines above it left open —
    ///     the walk <c>find_definition</c> does, and the only way to ask which side of a
    ///     declaration/implementation split a line sits on.
    /// </summary>
    private static List<Declared> DeclaredIn(string extension, string file)
    {
        var analyzer = Languages.Default.For(extension);
        var position = analyzer.Start;
        var declared = new List<Declared>();
        foreach (string line in file.Split('\n'))
        {
            if (analyzer.Declares(position, line).Value is { } what) declared.Add(what);
            position = analyzer.After(position, line);
        }

        return declared;
    }

    /// <summary>
    ///     Every appearance of the symbol in this file, in order, each placed from what the lines above
    ///     it left open — the walk <c>find_references</c> does over a file it read, and the only way to
    ///     ask about anything below the first line of a block comment or a multi-line literal.
    /// </summary>
    private static List<ReferenceKind> KindsIn(string extension, string file, string symbol)
    {
        var analyzer = Languages.Default.For(extension);
        var position = analyzer.Start;
        var kinds = new List<ReferenceKind>();
        foreach (string line in file.Split('\n'))
        {
            kinds.AddRange(analyzer.Occurrences(position, line, symbol).Select(placed => placed.Value));
            position = analyzer.After(position, line);
        }

        return kinds;
    }

    [Theory]
    // The WRITES section answers "what changes this?", and on a third of the codebases here it was
    // always empty: `:=` was not an assignment to anything, so every write read as something else.
    [InlineData("prg", "oOrder:Status := cNew")]
    [InlineData("pas", "  Order.Status := ntNew;")]
    [InlineData("pkb", "  l_order.Status := 1;")]
    public void Colon_equals_is_a_write(string extension, string line) =>
        Assert.Equal(ReferenceKind.Write, Kind(extension, line, "Status"));

    [Fact]
    public void Colon_equals_is_not_a_write_where_it_is_not_the_assignment()
    {
        // C# has no `:=`. Reporting one as a write would be the same mistake in the other direction:
        // an invented reference reported with the confidence of a real one.
        Assert.NotEqual(ReferenceKind.Write, Kind("cs", "Status := next", "Status"));
        // And `=` still is one, which is the half that must not regress.
        Assert.Equal(ReferenceKind.Write, Kind("cs", "        Status = next;", "Status"));
    }

    [Fact]
    public void Plain_equals_is_a_comparison_in_XSharp_and_not_a_write() =>
        Assert.NotEqual(ReferenceKind.Write, Kind("prg", "if Status = cNew", "Status"));

    [Fact]
    public void XSharp_sends_with_a_colon_and_still_calls_with_parentheses()
    {
        // `oCustomer:Name` used to land in UNPLACED, because only `.` was a receiver. Calls survived,
        // but only because the parenthesis test ignored what preceded them — confidently half-right.
        Assert.Equal(ReferenceKind.MemberAccess, Kind("prg", "cName := oCustomer:Name", "Name"));
        Assert.Equal(ReferenceKind.Call, Kind("prg", "oCustomer:Advance(1)", "Advance"));
        Assert.Equal(ReferenceKind.Call, Kind("prg", "Advance(1)", "Advance"));
        // The dot still reaches .NET members, so both are receivers here and neither is a type marker.
        Assert.Equal(ReferenceKind.MemberAccess, Kind("prg", "cName := oCustomer.Name", "Name"));
    }

    [Theory]
    [InlineData("prg", "#using System.Collections", "Collections")]
    [InlineData("prg", "#define MAX_ORDERS 10", "MAX_ORDERS")]
    [InlineData("prg", "#command WAIT => inkey(0)", "inkey")]
    [InlineData("cs", "#if MAX_ORDERS", "MAX_ORDERS")]
    [InlineData("cs", "#define MAX_ORDERS", "MAX_ORDERS")]
    [InlineData("cs", "#elif MAX_ORDERS", "MAX_ORDERS")]
    public void A_leading_hash_is_a_directive_and_not_a_comment(string extension, string line, string symbol) =>
        Assert.NotEqual(ReferenceKind.Comment, Kind(extension, line, symbol));

    /// <summary>
    ///     #265: what follows <c>#region</c>, <c>#endregion</c>, <c>#error</c> and <c>#warning</c> is a
    ///     label or a message, so a name in it is a mention in prose whatever punctuation stands beside
    ///     it. Since the char literal joined the C# profile (#240), an apostrophe there opened one that
    ///     ran to the end of the line, and the same name read as code or as a string by punctuation.
    /// </summary>
    [Theory]
    [InlineData("cs", "#region MAX_ORDERS")]
    [InlineData("cs", "#region Don't touch MAX_ORDERS")]
    [InlineData("cs", "#region Customer's 'MAX_ORDERS' code")]
    [InlineData("cs", "    #endregion MAX_ORDERS")]
    [InlineData("cs", "#error Can't build MAX_ORDERS")]
    [InlineData("cs", "#warning MAX_ORDERS isn't set")]
    // Whitespace may stand between the `#` and the directive's name, and it is still the directive
    // (#295).
    [InlineData("cs", "# region Don't touch MAX_ORDERS")]
    [InlineData("cs", "  #\tendregion MAX_ORDERS")]
    [InlineData("prg", "#  REGION Don't touch MAX_ORDERS")]
    [InlineData("prg", "#region MAX_ORDERS")]
    [InlineData("prg", "#REGION Don't touch MAX_ORDERS")]
    [InlineData("prg", "#endregion MAX_ORDERS")]
    [InlineData("prg", "#error Can't build MAX_ORDERS")]
    [InlineData("prg", "#warning MAX_ORDERS isn't set")]
    public void A_region_label_or_a_directive_message_is_prose(string extension, string line) =>
        Assert.Equal(ReferenceKind.Comment, Kind(extension, line, "MAX_ORDERS"));

    [Theory]
    // `#regional` is no directive, so nothing on it is prose on the strength of its first letters —
    // under X#'s case rule as much as under C#'s.
    [InlineData("cs", "#regional MAX_ORDERS")]
    [InlineData("prg", "#REGIONAL MAX_ORDERS")]
    [InlineData("cs", "# regional MAX_ORDERS")]
    public void A_directive_word_is_matched_whole(string extension, string line) =>
        Assert.NotEqual(ReferenceKind.Comment, Kind(extension, line, "MAX_ORDERS"));

    [Fact]
    public void An_apostrophe_in_a_region_label_leaves_the_next_line_as_code() =>
        Assert.Equal(
            [ReferenceKind.Comment, ReferenceKind.Call],
            KindsIn("cs", "#region Don't touch Advance\n    Advance(1);", "Advance"));

    [Theory]
    [InlineData("pas", "  ShowMessage('OrderStatus');")]
    [InlineData("sql", "SELECT 'OrderStatus' AS label")]
    [InlineData("pkb", "  raise_application_error(-20001, 'OrderStatus');")]
    public void Single_quotes_open_a_string(string extension, string line) =>
        Assert.Equal(ReferenceKind.StringLiteral, Kind(extension, line, "OrderStatus"));

    [Fact]
    public void A_doubled_quote_does_not_close_a_string() =>
        // `'it''s '` is one literal, not two — counting quotes would say the second one ended it and
        // report everything after as code.
        Assert.Equal(ReferenceKind.StringLiteral, Kind("sql", "SELECT 'it''s OrderStatus here'", "OrderStatus"));

    [Fact]
    public void Two_dashes_open_a_comment_only_where_they_do()
    {
        Assert.Equal(ReferenceKind.Comment, Kind("sql", "-- OrderStatus is the column", "OrderStatus"));
        // A decrement at the start of a trimmed line used to hide the rest of it.
        Assert.Equal(ReferenceKind.Call, Kind("ts", "--remaining; advance(OrderStatus)", "advance"));
    }

    [Theory]
    [InlineData("pas", "{ OrderStatus is a comment }")]
    [InlineData("pas", "(* OrderStatus is a comment *)")]
    [InlineData("pas", "// OrderStatus is a comment")]
    [InlineData("html", "<!-- OrderStatus is a comment -->")]
    [InlineData("css", "/* OrderStatus is a comment */")]
    [InlineData("prg", "&& OrderStatus is a comment")]
    public void Each_language_opens_a_comment_the_way_it_does(string extension, string line) =>
        Assert.Equal(ReferenceKind.Comment, Kind(extension, line, "OrderStatus"));

    [Fact]
    public void CSS_has_no_line_comment_and_Delphi_directives_are_not_comments()
    {
        // `//` is not a CSS comment, whatever a preprocessor does with it.
        Assert.NotEqual(ReferenceKind.Comment, Kind("css", "  border: 1px solid OrderStatus;", "OrderStatus"));
        // `{$IFDEF}` is a compiler directive sharing Delphi's comment brace.
        Assert.NotEqual(ReferenceKind.Comment, Kind("pas", "{$IFDEF OrderStatus}", "OrderStatus"));
    }

    [Theory]
    // SQL, PL/SQL, X# and Delphi are case-insensitive, and uppercase keywords are the dominant style
    // in the first two and common in legacy code in the other two. Reading them ordinally answered
    // "no declaration here" and then reported the line as a call — a wrong answer shaped like a right
    // one, for four of the nine languages registered.
    [InlineData("sql", "CREATE PROCEDURE Advance(n int)", "Advance")]
    [InlineData("sql", "CREATE OR REPLACE FUNCTION Advance(n int)", "Advance")]
    [InlineData("pkb", "CREATE OR REPLACE PROCEDURE Advance(n number) IS", "Advance")]
    [InlineData("prg", "METHOD Advance(n AS INT) AS VOID", "Advance")]
    [InlineData("pas", "PROCEDURE Advance(n: Integer);", "Advance")]
    public void A_shouted_keyword_still_declares(string extension, string line, string symbol)
    {
        Assert.Equal(ReferenceKind.Definition, Kind(extension, line, symbol));
        // And the two halves agree: the scope map and the counts must not read one line two ways.
        Assert.Equal(symbol, Declares(extension, line).Value?.Member);
    }

    /// <summary>
    ///     Whether the candidate predicate for one symbol keeps this line. Read with .NET's regex, which
    ///     reads the RE2 syntax these shapes are written in the same way; the engine's reading is
    ///     covered end-to-end in <see cref="DefinitionTests" />.
    /// </summary>
    private static bool Keeps(CandidateLines candidates, string line) =>
        candidates is CandidateLines.Re2Pattern shape
        && System.Text.RegularExpressions.Regex.IsMatch(line, shape.Pattern);

    private static bool IsCandidateFor(string extension, string symbol, string line) =>
        Keeps(Languages.Default.For(extension).DeclarationCandidatesFor(symbol), line);

    /// <summary>
    ///     #239: the candidate predicate for one name loses no line that declares it, in every shape —
    ///     the C-family member, the keyword and its qualifier, the type keyword, Delphi's preceding
    ///     type name and xBase's <c>define</c>.
    /// </summary>
    [Theory]
    [InlineData("cs", "    public void Advance(int n)")]
    [InlineData("cs", "public partial class OrderService : IService")]
    [InlineData("cs", "    public Dictionary<string, int> Counts { get; }")]
    [InlineData("prg", "method Advance(n as int) as void")]
    [InlineData("prg", "define FSEDIT_GET := 11")]
    [InlineData("pas", "procedure TCustomer.Save;")]
    [InlineData("pas", "  TCustomer = class(TObject)")]
    [InlineData("pkb", "CREATE OR REPLACE PROCEDURE Advance(n number) IS")]
    [InlineData("pks", "create package body app.orders as")]
    [InlineData("ts", "export const enum Direction {")]
    public void The_candidates_for_a_name_keep_every_line_that_declares_it(string extension, string line)
    {
        var declared = Declares(extension, line).Value;
        Assert.NotNull(declared);
        foreach (string? name in new[] { declared.Type, declared.Member })
            if (name is not null)
                Assert.True(IsCandidateFor(extension, name, line), $"{name} on: {line}");
    }

    /// <summary>
    ///     #239: a line shaped like a declaration of another name, which only mentions this one, is
    ///     not a candidate for it — the lines that used to fill the candidate cap.
    /// </summary>
    [Theory]
    [InlineData("cs", "    public void Run(OrderService s) { }", "OrderService")]
    [InlineData("cs", "    public OrderService Create()", "OrderService")]
    [InlineData("cs", "public class Report : OrderService", "OrderService")]
    [InlineData("cs", "public class OrderServiceFactory", "OrderService")]
    [InlineData("pas", "procedure Save(Customer: TCustomer);", "TCustomer")]
    public void A_line_that_only_mentions_the_name_is_not_a_candidate_for_it(string extension, string line,
        string symbol)
    {
        // Still a candidate for what it does declare, so the narrowing is by name and not by shape.
        Assert.True(Keeps(Languages.Default.For(extension).DeclarationCandidates, line));
        Assert.False(IsCandidateFor(extension, symbol, line));
    }

    [Fact]
    public void A_wrapped_boolean_clause_is_not_a_declaration() =>
        // `or` on its own would make the continuation line of any WHERE clause a declaration, and
        // DECLARATIONS is the section an agent trusts most.
        Assert.NotEqual(ReferenceKind.Definition, Kind("sql", "   or status = 1", "status"));

    [Fact]
    public void A_local_introduces_a_name_and_does_not_become_the_scope_under_it()
    {
        // The conflation this test was written for, now removed (#83). `local`, `instance`, `define`
        // and Delphi's `var` and `const` introduce a name and open no scope; one list answering both
        // questions meant they could only be kept out of the scope map by being left unread, so an
        // X# file of nothing but `define` lines declared nothing at all. Both halves are asserted
        // here, because the name and the missing scope are the two things that used to be one.
        var local = Declares("prg", "	local cLabel := self:Status").Value;
        Assert.Equal("cLabel", local?.Member);
        Assert.False(local?.OpensScope);

        var total = Declares("pas", "  var Total: Integer;").Value;
        Assert.Equal("Total", total?.Member);
        Assert.False(total?.OpensScope);

        var limit = Declares("pas", "  const Limit = 10;").Value;
        Assert.Equal("Limit", limit?.Member);
        Assert.False(limit?.OpensScope);
    }

    [Theory]
    // X#'s named constants, which are three files of AcsLib on their own and read as declaring
    // nothing until the two modifier sets came apart (#83, #71).
    [InlineData("prg", "define FSEDIT_GET := 11", "FSEDIT_GET")]
    [InlineData("prg", "define PD_ALLPAGES             := 0x00000000", "PD_ALLPAGES")]
    [InlineData("prg", "instance cLabel as string", "cLabel")]
    // The C family's, which were declarations before this and are still — and which were labelling
    // every reference under a local with themselves while they were.
    [InlineData("cs", "        const int Max = 10;", "Max")]
    [InlineData("cs", "        readonly Span<int> s = stackalloc int[4];", "s")]
    [InlineData("cs", "    private readonly ILogger _logger;", "_logger")]
    [InlineData("cs", "    public event EventHandler Changed;", "Changed")]
    public void A_name_a_modifier_introduces_without_opening_a_scope_is_still_a_declaration(
        string extension, string line, string member)
    {
        var declared = Declares(extension, line).Value;
        Assert.Equal(member, declared?.Member);
        // …and is never what a reference below it is labelled with. One name-only modifier decides it
        // whatever stands beside it: `private const string Pattern = "…";` declares a constant, and a
        // rule that asked whether any modifier opened a scope would have answered on the `private`.
        Assert.False(declared?.OpensScope);
    }

    [Theory]
    [InlineData("cs", "    public void Advance(int n)")]
    [InlineData("cs", "public class OrderService")]
    [InlineData("prg", "method Advance(n as int) as void")]
    [InlineData("pas", "procedure Advance(n: Integer);")]
    [InlineData("sql", "create or replace function advance(n int)")]
    // The SQL family names no scope list at all: its modifiers are the phrases that head a body, so
    // the two questions have one answer there and the labels are what they were.
    [InlineData("pkb", "CREATE OR REPLACE PROCEDURE Advance(n number) IS")]
    [InlineData("pks", "create package body app.orders as")]
    // A type opens a scope even when a name-only modifier shares the line with it. TypeScript's
    // `const enum` is the one that bites: read as a constant, every member inside it would be
    // labelled with whatever encloses the enum instead of with the enum.
    [InlineData("ts", "export const enum Direction {")]
    [InlineData("cs", "public readonly struct Point")]
    [InlineData("cs", "public readonly record struct Money(decimal Amount)")]
    public void A_routine_or_a_type_opens_the_scope_the_lines_under_it_sit_in(string extension, string line) =>
        Assert.True(Declares(extension, line).Value?.OpensScope);

    [Fact]
    public void A_profile_naming_one_modifier_set_reads_every_modifier_in_it_as_opening_a_scope()
    {
        // The back-compat half of #83: `ScopeModifiers` left empty means the two questions have one
        // answer, which is what every profile said before there were two of them and what the SQL
        // family, Delphi and the markup profiles still say.
        var one = Analyzer(["let", "func"], []);
        Assert.True(one.Declares(one.Start, "let Max = 10;").Value?.OpensScope);
        Assert.True(one.Declares(one.Start, "func Advance(n)").Value?.OpensScope);

        var two = Analyzer(["let", "func"], ["func"]);
        Assert.Equal("Max", two.Declares(two.Start, "let Max = 10;").Value?.Member);
        Assert.False(two.Declares(two.Start, "let Max = 10;").Value?.OpensScope);
        Assert.True(two.Declares(two.Start, "func Advance(n)").Value?.OpensScope);
        return;

        static Language.TextAnalyzer Analyzer(string[] modifiers, string[] scopes) =>
            new(new LanguageProfile("Toy", ["toy"])
            {
                DeclarationModifiers = modifiers, ScopeModifiers = scopes,
                DeclarationNamesFollowKeyword = true
            });
    }

    [Theory]
    // The second and later lines of a commented-out block. Read one line at a time, the first is a
    // comment and every line under it is whatever it looks like — a call, a write, an instantiation —
    // so find_references answered "who calls this?" with code deleted months ago, under the heading an
    // agent trusts most.
    [InlineData("cs", "/*\nadvance(1);\n*/")]
    [InlineData("pas", "{\nadvance(1);\n}")]
    [InlineData("pas", "(*\nadvance(1);\n*)")]
    [InlineData("html", "<!--\n<b onclick=advance(1)>\n-->")]
    [InlineData("css", "/*\n.a { color: advance(1) }\n*/")]
    [InlineData("sql", "/*\nadvance(1);\n*/")]
    [InlineData("pkb", "/*\nadvance(1);\n*/")]
    [InlineData("prg", "/*\nadvance(1)\n*/")]
    [InlineData("ts", "/*\nadvance(1);\n*/")]
    public void A_line_inside_a_block_comment_is_a_comment_wherever_the_block_opened(string extension, string file) =>
        Assert.Equal([ReferenceKind.Comment], KindsIn(extension, file, "advance"));

    [Theory]
    // A literal opened on an earlier line leaves every line after it reading as code, which is the
    // same hole in the other direction: an invented reference rather than a dropped one.
    [InlineData("cs", "var sql = @\"\nadvance(1)\n\";")]
    [InlineData("cs", "var sql = \"\"\"\nadvance(1)\n\"\"\";")]
    [InlineData("cs", "var sql = $@\"\nadvance(1)\n\";")]
    [InlineData("ts", "const q = `\nadvance(1)\n`;")]
    public void A_literal_that_spans_lines_is_a_string_mention(string extension, string file) =>
        Assert.Equal([ReferenceKind.StringLiteral], KindsIn(extension, file, "advance"));

    [Fact]
    public void A_raw_literal_is_closed_only_by_as_many_quotes_as_opened_it()
    {
        // A raw literal opened with four quotes may hold three, which is why it was written with four
        // (#240). Closed at the first `"""`, the rest of the literal was read as code.
        Assert.Equal([ReferenceKind.StringLiteral, ReferenceKind.StringLiteral, ReferenceKind.Call],
            KindsIn("cs", "var s = \"\"\"\"\n  \"\"\" advance(1)\n  advance(2)\n  \"\"\"\";\nadvance(3);", "advance"));
        // And a closer longer than the one it needs does not end a shorter literal early.
        Assert.Equal([ReferenceKind.StringLiteral, ReferenceKind.Call],
            KindsIn("cs", "var s = \"\"\"\"\"\n  \"\"\"\" advance(1)\n  \"\"\"\"\";\nadvance(2);", "advance"));
    }

    [Fact]
    public void A_CSharp_char_literal_is_not_the_start_of_a_string()
    {
        // `'"'` is one character, and without a char literal its quote opened a string that ran to the
        // end of the line and turned the call after it into a mention (#240).
        Assert.Equal(ReferenceKind.Call, Kind("cs", "if (c == '\"') return ParseQuoted(reader);", "ParseQuoted"));
        Assert.Equal(ReferenceKind.Call, Kind("cs", "if (c == '\\'') return ParseQuoted(reader);", "ParseQuoted"));
        // A stray apostrophe opens nothing, on its own line or the next (#295).
        Assert.Equal([ReferenceKind.Call, ReferenceKind.Call],
            KindsIn("cs", "x = 'advance(1)\nadvance(2);", "advance"));
    }

    /// <summary>
    ///     #295: a char literal is one character or one escape between two apostrophes, so an
    ///     apostrophe with no closer in that shape is not one. Read as an opener, it ran to the end of
    ///     the line and every name after it was taken for literal text.
    /// </summary>
    [Theory]
    [InlineData("x = it's Advance(1);")]
    [InlineData("x = 'ab' + Advance(1);")]
    [InlineData("x = '' + Advance(1);")]
    [InlineData("x = '\\' + Advance(1);")]
    [InlineData("x = ' ; Advance(1); var c = 'y';")]
    [InlineData("var s = $\"{(c == '\\'' ? Advance(1) : 0)}\";")]
    public void An_apostrophe_without_a_closer_in_char_shape_hides_nothing(string line) =>
        Assert.Equal(ReferenceKind.Call, Kind("cs", line, "Advance"));

    [Theory]
    [InlineData("var c = 'A';", "A")]
    [InlineData("f('\\'', 'A');", "A")]
    [InlineData("f('\\\\', 'A');", "A")]
    [InlineData("var c = '\\n';", "n")]
    [InlineData("var c = '\\x41';", "x41")]
    [InlineData("var c = '\\u0041';", "u0041")]
    [InlineData("var c = '\\U00000041';", "U00000041")]
    public void Real_char_literals_are_still_literals(string line, string symbol) =>
        Assert.Equal(ReferenceKind.StringLiteral, Kind("cs", line, symbol));

    [Theory]
    // At the top level a leading `*` is a multiplication or a generator method, and a `/* */`
    // continuation line is always inside the comment the scan already carries (#240).
    [InlineData("cs", "    * quantity;", "quantity", ReferenceKind.Other)]
    [InlineData("ts", "  *entries() {", "entries", ReferenceKind.Call)]
    [InlineData("js", "  *entries() {", "entries", ReferenceKind.Call)]
    public void A_leading_star_outside_a_comment_is_code(string extension, string line, string symbol,
        ReferenceKind expected) =>
        Assert.Equal(expected, Kind(extension, line, symbol));

    [Fact]
    public void A_leading_star_is_still_a_comment_where_the_language_writes_one()
    {
        // xBase writes a whole-line comment with a leading `*`, and the doc block's continuation stays
        // prose because the block around it is carried.
        Assert.Equal(ReferenceKind.Comment, Kind("prg", "* advance the order", "advance"));
        Assert.Equal([ReferenceKind.Comment], KindsIn("cs", "/**\n * advance(1)\n */", "advance"));
    }

    [Fact]
    public void An_interpolated_literal_that_spans_lines_is_text_around_its_holes()
    {
        // Both halves on one file: the text of a raw interpolated literal is a mention on its second
        // line, and the hole in it is still live code there.
        Assert.Equal([ReferenceKind.StringLiteral, ReferenceKind.Call],
            KindsIn("cs", "var s = $\"\"\"\n  advance is {advance(1)}\n  \"\"\";", "advance"));
        Assert.Equal([ReferenceKind.StringLiteral, ReferenceKind.Call],
            KindsIn("cs", "var s = $@\"\n  advance is {advance(1)}\n  \";", "advance"));
    }

    [Fact]
    public void A_closer_ends_it_and_the_code_after_it_on_that_line_is_code()
    {
        // The line holding the closer is not itself inside the thing it closes, and what follows it is
        // ordinary code: a state machine that reset only at the next line would lose a whole line of it.
        Assert.Equal([ReferenceKind.Comment, ReferenceKind.Call],
            KindsIn("cs", "/*\nadvance(1);\n*/ advance(2);", "advance"));
        Assert.Equal([ReferenceKind.StringLiteral, ReferenceKind.Call],
            KindsIn("cs", "var sql = @\"\nadvance(1)\n\"; advance(2);", "advance"));
        // And a block that opens after code on its line leaves that code alone.
        Assert.Equal([ReferenceKind.Call, ReferenceKind.Comment],
            KindsIn("cs", "advance(1); /* advance(2)\n*/", "advance"));
    }

    [Fact]
    public void An_unterminated_ordinary_literal_does_not_swallow_the_file()
    {
        // `"` does not span lines in C#, so an odd one is a typo or a quote in prose the profile does
        // not know — not a claim about every line below it. Reading it as open would turn the rest of
        // the file into a string, which is the loudest way this could be wrong.
        Assert.Equal([ReferenceKind.StringLiteral, ReferenceKind.Call],
            KindsIn("cs", "Log(\"advance(1)\nadvance(2);", "advance"));
        // And at any depth, not only the outermost. What carries to the next line is the run of spans
        // from the outside in that all carry, so an ordinary quote left open inside a template
        // literal's hole ends with its line while the template around it goes on.
        Assert.Equal([ReferenceKind.StringLiteral, ReferenceKind.Call],
            KindsIn("ts", "const q = `${ parse(\"advance(1)\nadvance(2)} a`;", "advance"));
    }

    [Fact]
    public void A_reference_the_scan_never_reached_is_kept_and_left_unplaced()
    {
        // What a caller that could not read the lines above a match is handed. Every shape below the
        // lexical state — a call, a write, a declaration — asserts that the line is live code, and
        // that is exactly what is not known here.
        var analyzer = Languages.Default.For("cs");
        var placed = analyzer.Occurrences(FilePosition.Unknown, "        advance(1);", "advance");
        Assert.Equal(ReferenceKind.Other, Assert.Single(placed).Value);
        Assert.Equal(Lexical.Unknown, analyzer.StateAt(FilePosition.Unknown, "        advance(1);", 8).Value);
        // A position another analyser made says nothing about this one's tables, so it is unknown too
        // rather than read as whatever those indexes happen to point at.
        Assert.Equal(Lexical.Unknown,
            analyzer.StateAt(Languages.Default.For("pas").Start, "        advance(1);", 8).Value);
    }

    [Fact]
    public void A_call_inside_an_interpolation_hole_is_still_a_call()
    {
        // A delimiter that does not know `${…}` and `{…}` are code turns every call made inside one
        // into a string mention, which loses real calls — the reason both were left out of the
        // profiles until the scan could carry what a hole needs.
        Assert.Equal(ReferenceKind.Call, Kind("ts", "const label = `a ${advance(1)} b`;", "advance"));
        Assert.Equal(ReferenceKind.Call, Kind("cs", "var label = $\"a {advance(1)} b\";", "advance"));
        Assert.Equal(ReferenceKind.Call, Kind("cs", "var label = $@\"a {advance(1)} b\";", "advance"));
        // The text around a hole is still text, and a doubled brace is text rather than a hole.
        Assert.Equal(ReferenceKind.StringLiteral, Kind("cs", "var label = $\"{{advance(1)}} {x}\";", "advance"));
        // A brace nested inside a hole does not end it a character early.
        Assert.Equal(ReferenceKind.Call,
            Kind("cs", "var label = $\"{string.Join(\",\", new[] { 1 })} {advance(1)}\";", "advance"));
        // And a hole in a literal that spans lines is code on the line below too.
        Assert.Equal([ReferenceKind.Call], KindsIn("ts", "const q = `a\n${advance(1)}\n`;", "advance"));
    }

    [Fact]
    public void A_long_line_is_scanned_once_and_not_once_per_question()
    {
        // A minified bundle is one line of several million characters, and files up to
        // Index:MaxFileBytes are indexed. Asked one appearance at a time, classifying such a line
        // re-walked it per appearance and re-ran the declaration regex over it per appearance, which
        // turned a single find_references into minutes inside one tool call. This line is 640 KB with
        // 20,000 appearances on it: linear in the line it stays well under the bound, quadratic in it
        // nothing does.
        string line = string.Concat(Enumerable.Repeat("var x = \"http://a\"; advance(1); ", 20_000));
        var analyzer = Languages.Default.For("ts");

        var started = System.Diagnostics.Stopwatch.StartNew();
        int calls = analyzer.Occurrences(analyzer.Start, line, "advance").Count(k => k.Value == ReferenceKind.Call);
        started.Stop();

        Assert.Equal(20_000, calls);
        // Generous against a loaded CI box: the measured time is a small fraction of this.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2),
            $"classifying one long line took {started.Elapsed}");
    }

    [Fact]
    public void An_extension_no_profile_covers_falls_back_to_the_old_rules()
    {
        var fallback = Languages.Default.For("wibble");
        Assert.Null(fallback.Language);
        Assert.Equal(ReferenceKind.Comment, Kind("wibble", "// OrderStatus", "OrderStatus"));
        Assert.Equal(ReferenceKind.StringLiteral, Kind("wibble", "Log(\"OrderStatus\")", "OrderStatus"));
        Assert.Equal(ReferenceKind.Write, Kind("wibble", "        Status = next;", "Status"));
        // Including the rules that are wrong for some language somewhere: the fallback is today's
        // behaviour on purpose, so that no project regresses on a language nobody declared.
        Assert.Equal(ReferenceKind.Comment, Kind("wibble", "#define OrderStatus 1", "OrderStatus"));
        Assert.Equal(ReferenceKind.Comment, Kind("wibble", "-- OrderStatus", "OrderStatus"));
    }

    [Fact]
    public void Adding_a_language_is_one_registration_and_touches_no_caller()
    {
        var invented = new LanguageProfile("Wibble", ["wib"])
        {
            LineComments = ["%%"],
            Strings = [new StringDelimiter("|", "|", StringEscape.None)],
            AssignmentOperators = ["<-"],
            MemberAccessOperators = ["->"]
        };
        var registry = Languages.Default.With(new Language.TextAnalyzer(invented));

        var analyzer = registry.For("wib");
        Assert.Equal("Wibble", analyzer.Language);
        Assert.Equal(ReferenceKind.Comment, Kind(analyzer, "%% OrderStatus", "OrderStatus"));
        Assert.Equal(ReferenceKind.StringLiteral, Kind(analyzer, "log |OrderStatus|", "OrderStatus"));
        Assert.Equal(ReferenceKind.Write, Kind(analyzer, "OrderStatus <- 1", "OrderStatus"));
        Assert.Equal(ReferenceKind.MemberAccess, Kind(analyzer, "order->OrderStatus", "OrderStatus"));
        // The languages already registered are untouched, and so is the registry this was built from.
        Assert.Equal("C#", registry.For("cs").Language);
        Assert.Null(Languages.Default.For("wib").Language);
    }

    [Fact]
    public void A_better_implementation_can_be_laid_over_a_language_already_covered()
    {
        var registry = Languages.Default.With(new StubAnalyzer());

        var analyzer = registry.For("cs");
        // The caller asks the same questions of the same interface and gets a parsed answer.
        Assert.Equal("C# (parsed)", analyzer.Language);
        Assert.Equal(Evidence.Parsed, analyzer.Occurrences(analyzer.Start, "Status = next;", "Status")[0].Evidence);
        Assert.Equal(ReferenceKind.Definition, Kind(analyzer, "Status = next;", "Status"));
        // The text analyser is still there for everything the stub did not claim — including `.csx`,
        // which the C# profile claims and the stub does not.
        Assert.Equal("X#", registry.For("prg").Language);
        Assert.Equal("C#", registry.For("csx").Language);
        var xbase = registry.For("prg");
        Assert.Equal(Evidence.Text, xbase.Occurrences(xbase.Start, "oOrder:Status := 1", "Status")[0].Evidence);
    }

    [Fact]
    public void The_last_registration_wins_however_many_there_have_been()
    {
        // The overlay claims `cs` while the C# profile also claims `csx`, so both stay in the map.
        // Recovering the order from the map rather than keeping it let the loser take `cs` back.
        var registry = Languages.Default.With(new StubAnalyzer());
        for (int i = 0; i < 6; i++)
            registry = registry.With(new Language.TextAnalyzer(new LanguageProfile($"Filler{i}", [$"f{i}"])));

        Assert.Equal("C# (parsed)", registry.For("cs").Language);
        Assert.Equal("C#", registry.For("csx").Language);
        // And re-registering the text profile afterwards takes it back, which is what order means.
        Assert.Equal("C#", registry.With(Languages.Default.For("csx")).For("cs").Language);
    }

    [Fact]
    public void Every_answer_says_how_it_was_reached()
    {
        var analyzer = Languages.Default.For("cs");
        Assert.Equal(Evidence.Text, analyzer.StateAt(analyzer.Start, "// x", 3).Evidence);
        Assert.Equal(Evidence.Text, analyzer.Declares(analyzer.Start, "public class Order").Evidence);
        Assert.Equal(Evidence.Text, analyzer.ImportsOn(analyzer.Start, "using System;").Evidence);
        Assert.Equal(Evidence.Text, analyzer.IsGenerated("src/Order.g.cs").Evidence);
        Assert.Equal(Evidence.Text, analyzer.Occurrences(analyzer.Start, "Order x;", "Order")[0].Evidence);
    }

    [Fact]
    public void An_import_line_is_told_apart_from_what_it_imports()
    {
        Assert.Equal([("System.Text", ImportShape.Module)], Imports("cs", "using System.Text;"));
        Assert.Equal([("System.Text.Json", ImportShape.Module)], Imports("cs", "global using System.Text.Json;"));
        Assert.Equal([("System.Collections", ImportShape.Module)], Imports("prg", "#using System.Collections"));
        Assert.Equal([("Common.vh", ImportShape.Path)], Imports("prg", "#include \"Common.vh\""));
        Assert.Empty(Imports("cs", "        Status = next;"));
    }

    [Fact]
    public void The_forms_each_language_writes_are_the_ones_it_reads()
    {
        // A TypeScript specifier is a path however it is written, and the names in front of it are
        // what the file takes out of the module rather than what it depends on.
        Assert.Equal([("./orders", ImportShape.Path)], Imports("ts", "import { a, b } from \"./orders\";"));
        Assert.Equal([("./orders", ImportShape.Path)], Imports("ts", "import \"./orders\";"));
        Assert.Equal([("./late", ImportShape.Path)], Imports("ts", "const m = await import(\"./late\");"));
        Assert.Equal([("./util", ImportShape.Path)], Imports("js", "const u = require(\"./util\");"));

        // The tag is the opener and the attribute is what is read out of it, so an anchor is not a
        // dependency and a `data-src` is not a `src`.
        Assert.Equal([("app.js", ImportShape.Path)], Imports("html", "  <script defer src=\"app.js\"></script>"));
        Assert.Equal([("site.css", ImportShape.Path)], Imports("html", "<link rel=\"stylesheet\" href=\"site.css\">"));
        Assert.Empty(Imports("html", "<a href=\"/about\">About</a>"));
        // Markup puts several of them on one line, and a form that stopped at the first would lose
        // the rest.
        Assert.Equal([("a.css", ImportShape.Path), ("b.css", ImportShape.Path)],
            Imports("html", "<link href=\"a.css\"><link href=\"b.css\">"));

        // Three spellings of one thing, and all three answer with the file.
        Assert.Equal([("base.css", ImportShape.Path)], Imports("css", "@import \"base.css\";"));
        Assert.Equal([("base.css", ImportShape.Path)], Imports("css", "@import url(\"base.css\");"));
        Assert.Equal([("logo.png", ImportShape.Path)], Imports("css", "  background: url(logo.png) no-repeat;"));

        // SQL names no file it depends on, and says so by having no forms at all rather than by
        // answering with an empty list that reads as "this depends on nothing".
        Assert.False(Languages.Default.For("sql").HasImports);
        Assert.False(Languages.Default.For("pks").HasImports);
        Assert.True(Languages.Default.For("cs").HasImports);
    }

    /// <summary>
    ///     The word that opens a C# using directive also opens a using statement and a using block,
    ///     and only the first is an import. An opener alone cannot tell them apart; the clause can,
    ///     because a module name is a dotted identifier and an expression is not.
    /// </summary>
    [Fact]
    public void A_using_statement_is_not_a_using_directive()
    {
        Assert.Empty(Imports("cs", "        using var reader = new StreamReader(path);"));
        Assert.Empty(Imports("cs", "        using (var scope = provider.CreateScope())"));
        Assert.Empty(Imports("cs", "        using var a = b.c;"));
        // And the directive still is one, which is the half that must not regress.
        Assert.Equal([("System.Text", ImportShape.Module)], Imports("cs", "using System.Text;"));
        // `static` is a qualifier in front of the name, and the name is what is imported.
        Assert.Equal([("System.Math", ImportShape.Module)], Imports("cs", "using static System.Math;"));
        // An alias imports the type on the right of the `=`, not the name it binds on the left.
        Assert.Equal([("System.Windows.Controls.Grid", ImportShape.Module)],
            Imports("cs", "using Grid = System.Windows.Controls.Grid;"));
    }

    [Fact]
    public void An_include_is_read_however_its_header_is_quoted() =>
        // The angle-bracket spelling is the dominant one in X#, and a reader that knew only `"…"`
        // answered that a file including only system headers imports nothing.
        Assert.Equal([("Set_Ansi.ch", ImportShape.Path)], Imports("prg", "#include <Set_Ansi.ch>"));

    [Fact]
    public void A_file_says_what_it_declares_itself_to_be()
    {
        Assert.Equal("Orders.Domain", Declared("cs", "namespace Orders.Domain;"));
        Assert.Equal("Orders.Domain", Declared("cs", "namespace Orders.Domain"));
        Assert.Equal("Orders.Domain", Declared("cs", "namespace Orders.Domain {"));
        Assert.Equal("Customers", Declared("pas", "unit Customers;"));
        // What a file declares is not what it imports, and the two must not end up in one list.
        Assert.Empty(Imports("cs", "namespace Orders.Domain;"));
    }

    /// <summary>
    ///     The clause the ticket was written around: comma-separated, as many lines as it likes until
    ///     the <c>;</c>, and written twice in a unit. Read one line at a time it yields the first unit
    ///     of each clause and loses every unit under it.
    /// </summary>
    [Fact]
    public void A_Delphi_uses_clause_is_read_across_its_lines_and_in_both_sections()
    {
        string[] unit =
        [
            "unit Customers;",
            "",
            "interface",
            "",
            "uses",
            "  Vcl.Forms, Vcl.Dialogs,",
            "  System.SysUtils;",
            "",
            "implementation",
            "",
            "uses Orders, Invoices;",
            "",
            "end."
        ];

        var (names, declared) = Walk("pas", unit);

        Assert.Equal("Customers", declared);
        Assert.Equal(
            ["Vcl.Forms", "Vcl.Dialogs", "System.SysUtils", "Orders", "Invoices"],
            names.Select(name => name.Name));
        Assert.All(names, name => Assert.Equal(ImportShape.Module, name.Shape));
    }

    /// <summary>
    ///     A clause whose terminator this cannot see — hidden in a <c>{ }</c> comment, or never
    ///     written — would otherwise stay open to the end of the file and report every line below it
    ///     as an import. One line is the most a missed <c>;</c> may cost.
    /// </summary>
    [Fact]
    public void An_unclosed_uses_clause_does_not_swallow_the_file()
    {
        var (names, _) = Walk("pas",
        [
            "unit Broken;",
            "interface",
            "uses",
            "  Vcl.Forms,",
            "procedure Advance(n: Integer);",
            "implementation",
            "procedure Advance(n: Integer);",
            "begin",
            "end;",
            "end."
        ]);

        // The units that were written are read; the line that cannot be part of a name list ends the
        // clause, and nothing below it is reported as an import.
        Assert.Equal(["Vcl.Forms"], names.Select(name => name.Name));
    }

    [Fact]
    public void An_import_written_in_prose_is_not_an_import()
    {
        // Both lines are shaped exactly like an import; only the lines above them say they are not
        // one, which is why the walk carries a position at all.
        var (commented, _) = Walk("cs",
            ["/* the old shape:", "using System.Text;", "*/", "using System.Linq;"]);
        Assert.Equal(["System.Linq"], commented.Select(name => name.Name));

        // A trailing note is not part of the clause either.
        Assert.Equal([("System.Text", ImportShape.Module)], Imports("cs", "using System.Text; // for the builder"));
        Assert.Empty(Imports("cs", "// using System.Text;"));
    }

    [Fact]
    public void A_line_read_without_the_lines_above_it_still_names_its_imports()
    {
        // The interface promises an answer from the line alone for a caller that has not walked the
        // file (#240). A build hands one on once nesting outruns the scan, and every `using` below that
        // point was silently dropped.
        var analyzer = Languages.Default.For("cs");
        Assert.Equal(["System.Text"],
            analyzer.ImportsOn(FilePosition.Unknown, "using System.Text;").Value.Imports.Select(n => n.Name));
        // Read on its own, the line still cuts its own comment away.
        Assert.Equal(["System.Text"],
            analyzer.ImportsOn(FilePosition.Unknown, "using System.Text; // for the builder").Value.Imports
                .Select(n => n.Name));
    }

    [Fact]
    public void A_unit_on_the_continuation_of_a_uses_clause_is_an_import_reference() =>
        // The build already read `Vcl.Dialogs` as an import off the open clause; find_references read
        // only the line's own opener and reported the same name as a member access (#240).
        // The `;` closes it, and the unit used on the line after is code again.
        Assert.Equal([ReferenceKind.Import, ReferenceKind.TypeUse],
            KindsIn("pas", "uses\n  Vcl.Forms, Vcl.Dialogs;\nDialogs.Show;", "Dialogs"));

    private static (string Name, ImportShape Shape)[] Imports(string extension, string line)
    {
        var analyzer = Languages.Default.For(extension);
        return [.. analyzer.ImportsOn(analyzer.Start, line).Value.Imports.Select(n => (n.Name, n.Shape))];
    }

    private static string? Declared(string extension, string line)
    {
        var analyzer = Languages.Default.For(extension);
        return analyzer.ImportsOn(analyzer.Start, line).Value.Declares;
    }

    /// <summary>The file walked as a build walks it: a position per line, carried.</summary>
    private static (List<ImportedName> Names, string? Declared) Walk(string extension, string[] lines)
    {
        var analyzer = Languages.Default.For(extension);
        var position = analyzer.Start;
        var names = new List<ImportedName>();
        string? declared = null;
        foreach (string line in lines)
        {
            var found = analyzer.ImportsOn(position, line).Value;
            names.AddRange(found.Imports);
            declared ??= found.Declares;
            position = analyzer.After(position, line);
        }

        return (names, declared);
    }

    [Fact]
    public void A_generated_file_is_named_as_one()
    {
        Assert.True(Languages.Default.For("cs").IsGenerated("main/src/Order.g.cs").Value);
        Assert.True(Languages.Default.For("prg").IsGenerated("main/src/Order_vo.prg").Value);
        Assert.False(Languages.Default.For("cs").IsGenerated("main/src/Order.cs").Value);
        // Nothing is claimed for a language with no pattern for it, which is weaker than a guess.
        Assert.False(Languages.Default.For("sql").IsGenerated("main/src/Order.sql").Value);
    }

    [Fact]
    public void The_languages_that_split_declaration_from_implementation_say_so()
    {
        Assert.True(Languages.Default.For("pas").SeparatesDeclarationFromImplementation);
        Assert.True(Languages.Default.For("pkb").SeparatesDeclarationFromImplementation);
        Assert.False(Languages.Default.For("cs").SeparatesDeclarationFromImplementation);
    }

    [Fact]
    public void A_declaration_is_read_in_each_shape_a_language_writes_it()
    {
        Assert.Equal("Advance", Declares("cs", "    public void Advance(int n)").Value?.Member);
        Assert.Equal("OrderService", Declares("cs", "public class OrderService").Value?.Type);
        // The name follows the introducing word here, with the return type after it rather than before.
        Assert.Equal("Advance", Declares("prg", "method Advance(n as int) as void").Value?.Member);
        Assert.Equal("Advance", Declares("pas", "procedure Advance(n: Integer);").Value?.Member);
        Assert.Equal("advance", Declares("sql", "create or replace function advance(n int)").Value?.Member);
        Assert.Null(Declares("cs", "        Status = next;").Value);
    }

    [Theory]
    // A field is a member, and CONTEXT.md's _Declaration_ says a member is one. The C-family shape
    // used to require the name to be followed by `(`, `<`, `{` or `=`, so whether a field appeared
    // depended on whether it had been given an initialiser — the same member, read two ways, and the
    // opposite of what the xBase/Delphi/SQL shape beside it has always answered with its `;`.
    [InlineData("public int Count;", "Count")]
    [InlineData("    private readonly Foo _bar;", "_bar")]
    // A generic type is read too, and reading it is what corrects the worse of the two old answers:
    // `private List<string>? _names;` used to match on the `<` of its own type argument, so the line
    // was reported as declaring `List` — a false declaration under the heading an agent trusts most,
    // where a bare `int` field was merely a miss. Three lines of this repo were named after their
    // type that way.
    [InlineData("    private List<string>? _names;", "_names")]
    // `event` was not a C-family modifier at all, so this line was found by neither shape.
    [InlineData("    public event EventHandler? Changed;", "Changed")]
    // The initialised forms answer what they always answered; this adds a shape, it replaces none.
    [InlineData("    private const string Pattern = \"x\";", "Pattern")]
    public void A_C_family_member_declares_whether_or_not_it_was_given_an_initialiser(
        string line, string member) =>
        Assert.Equal(member, Declares("cs", line).Value?.Member);

    [Theory]
    // A type argument list is written with spaces after its commas by every formatter there is, and
    // the type was a character class that held none — so the shape stopped at the first space and
    // `Dictionary<string, int> _byName;` was a miss, initialised or not. The list is now read as the
    // bracketed group it is, which is also what lets it nest.
    [InlineData("    private Dictionary<string, int> _byName;", "_byName")]
    [InlineData("    private Dictionary<string, int> _byName = new();", "_byName")]
    [InlineData("    public Dictionary<string, List<Foo>> Grouped { get; set; }", "Grouped")]
    [InlineData("    private readonly Dictionary<string, Func<int, bool>> _rules = [];", "_rules")]
    [InlineData("    public Task<IReadOnlyList<Order>> LoadAsync(int id)", "LoadAsync")]
    // The markers that ride after the closing bracket, which the old class held and this must keep.
    [InlineData("    private List<string>[] _pages;", "_pages")]
    [InlineData("    private Dictionary<string, int>? _maybe;", "_maybe")]
    public void A_type_argument_list_is_read_whole_however_it_is_spaced(string line, string member) =>
        Assert.Equal(member, Declares("cs", line).Value?.Member);

    [Fact]
    public void A_generic_field_is_named_after_itself_and_not_after_its_type() =>
        // The regression this shape most needed to fix, and the one a miss hid: with nothing but
        // `[\(<{=]` to close on, the `<` opening a type argument was an opener, so the name captured
        // was the type's. It is a declaration that exists, points at the right line, and carries the
        // wrong name — worse than the bare field's silence, because an agent acts on it.
        Assert.Equal("_logger", Declares("cs", "    private readonly ILogger<Thing> _logger;").Value?.Member);

    [Theory]
    // The other half of the same rule, and the one that pays for it: a statement carries no
    // declaration modifier, so ending at a `;` is not on its own what makes a line a declaration.
    // DECLARATIONS is the section an agent trusts most, and a false one there is worse than a miss.
    [InlineData("        return count;")]
    [InlineData("        var total = 0;")]
    [InlineData("        await using var scope = new Thing();")]
    [InlineData("        _bar.Advance(1);")]
    [InlineData("        new Thing();")]
    // `new` is a C-family modifier, so a constructor call whose type argument list holds a space used
    // to read as modifier, type, name, `<` — and was reported as declaring `Dictionary`. Twelve lines
    // of this repo were, every one of them an argument in the middle of a call. Reading the argument
    // list whole is what ends it: there is no name after the type, so there is no declaration.
    [InlineData("            new Dictionary<string, Dictionary<string, string>>")]
    [InlineData("            new Dictionary<string, string> { [\"a\"] = \"b\" });")]
    public void A_statement_is_not_a_declaration_just_because_it_ends_at_a_semicolon(string line) =>
        Assert.Null(Declares("cs", line).Value);

    [Fact]
    public void Re_exporting_a_binding_is_not_declaring_it() =>
        // TypeScript and JavaScript share the C-family modifier list, so `export` is one and
        // `export default thing;` is modifier, word, word, `;` — the field shape exactly, while it
        // names a binding declared elsewhere. `NonTypeKeywords` is what refuses it.
        // The `export default function thing() {}` beside it is a miss and not the answer: no shape
        // reads it today, and it was a miss before the `;` too. Naming it here would be inventing a
        // behaviour, and a form no profile knows is one this does not find rather than one that is
        // not there (CONTEXT.md, Declaration).
        Assert.Null(Declares("ts", "export default thing;").Value);

    [Fact]
    public void A_Delphi_unit_announces_a_routine_in_one_section_and_writes_it_in_the_other()
    {
        var declared = DeclaredIn("pas", """
                                         unit Customers;

                                         interface

                                         type
                                           TCustomer = class(TObject)
                                             procedure Save;
                                           end;

                                         implementation

                                         procedure TCustomer.Save;
                                         begin
                                         end;

                                         end.
                                         """);

        // The class and the announcement sit in the interface section, the body under implementation,
        // and the two `procedure` lines are indistinguishable without knowing which section they are in.
        Assert.Equal([
                new Declared("TCustomer", null, DeclarationRole.Declaration),
                new Declared(null, "Save", DeclarationRole.Declaration),
                new Declared("TCustomer", "Save", DeclarationRole.Implementation)
            ],
            declared);
    }

    [Fact]
    public void A_PLSQL_spec_and_body_are_told_apart_though_neither_file_mentions_the_other()
    {
        var spec = DeclaredIn("pks", """
                                     create or replace package pkg_orders as
                                       procedure add_order(p_id number);
                                     end pkg_orders;
                                     """);
        var body = DeclaredIn("pkb", """
                                     create or replace package body pkg_orders as
                                       procedure add_order(p_id number) is
                                       begin
                                         null;
                                       end add_order;
                                     end pkg_orders;
                                     """);

        // The header line is itself the first declaration of its half, so the role holds from it and
        // not from the line after it.
        Assert.Equal(DeclarationRole.Declaration, spec[0].Role);
        Assert.Equal([new Declared(null, "add_order", DeclarationRole.Declaration)], spec[1..]);
        Assert.Equal(DeclarationRole.Implementation, body[0].Role);
        Assert.Equal([new Declared(null, "add_order", DeclarationRole.Implementation)], body[1..]);
    }

    [Fact]
    public void A_language_that_declares_once_says_nothing_about_which_side_it_is_on()
    {
        // C#, X#, TypeScript and JavaScript declare and implement in one place, so a role would be a
        // distinction the language does not draw.
        Assert.Null(Declares("cs", "public void Advance(int n)").Value?.Role);
        Assert.Null(Declares("prg", "method Advance(n as int) as void").Value?.Role);
        // And a Delphi line read on its own, by a caller that could not walk the file to it, is
        // unplaced rather than guessed at.
        Assert.Null(Declares("pas", "procedure Save;").Value?.Role);
    }

    [Fact]
    public void The_word_that_moves_a_file_between_sections_is_read_as_a_word()
    {
        // `implementation` inside a comment moves nothing, or every routine below a commented-out
        // block would be reported as a body.
        var commented = DeclaredIn("pas", "interface\n{\nimplementation\n}\nprocedure Save;");
        Assert.Equal([new Declared(null, "Save", DeclarationRole.Declaration)], commented);

        // And a formatter's extra spaces do not hide a marker, while a longer word is not one.
        Assert.Equal(DeclarationRole.Implementation,
            DeclaredIn("pkb", "create  or replace   package body app.orders as\nprocedure Save;")[^1].Role);
        Assert.Equal(DeclarationRole.Declaration,
            DeclaredIn("pas", "interface\nimplementations := 1;\nprocedure Save;")[^1].Role);
    }

    [Fact]
    public void Each_language_declares_in_the_shapes_it_writes()
    {
        // X#'s five forms, which no C-family pattern reads: the name follows the introducing word and
        // the return type follows the name.
        Assert.Equal("Advance", Declares("prg", "function Advance(n as int) as int").Value?.Member);
        Assert.Equal("Advance", Declares("prg", "method Advance(n as int) as void").Value?.Member);
        Assert.Equal("Status", Declares("prg", "access Status as string").Value?.Member);
        Assert.Equal("Status", Declares("prg", "assign Status(value as string)").Value?.Member);
        Assert.Equal("OrderService", Declares("prg", "class OrderService").Value?.Type);

        // Delphi's, including the qualified head an implementation is written with.
        Assert.Equal("Create", Declares("pas", "constructor Create(AOwner: TComponent);").Value?.Member);
        Assert.Equal("Destroy", Declares("pas", "destructor Destroy; override;").Value?.Member);
        Assert.Equal(new Declared("TCustomer", "Save", null), Declares("pas", "procedure TCustomer.Save;").Value);
        Assert.Equal("TCustomer", Declares("pas", "  TCustomer = class(TObject)").Value?.Type);

        // The SQL family's CREATE forms, whose body is opened by a word and not by a bracket.
        Assert.Equal("advance", Declares("sql", "create or replace procedure advance(n int)").Value?.Member);
        Assert.Equal("orders", Declares("sql", "create view orders as").Value?.Member);
        Assert.Equal("pkg_orders",
            Declares("pkb", "create or replace package body pkg_orders as").Value?.Member);
    }

    [Fact]
    public void Extensions_still_name_a_language_for_the_overview()
    {
        Assert.Equal("X#", Languages.Of("prg"));
        Assert.Equal("X#", Languages.Of(".VH"));
        Assert.Null(Languages.Of("wibble"));
        Assert.Equal(("X#", true), Languages.Name("prg"));
        Assert.Equal((".wibble", false), Languages.Name("wibble"));
        Assert.Equal((Languages.NoExtension, false), Languages.Name(""));
    }


    /// <summary>
    ///     What a Roslyn- or tree-sitter-backed analyser would be, as far as the seam is concerned: the
    ///     same five questions, answered another way, claiming an extension a profile already covers.
    ///     It answers nonsense on purpose — a real one would not — so that a test can see which of the
    ///     two replied.
    /// </summary>
    private sealed class StubAnalyzer : ILanguageAnalyzer
    {
        public string Language => "C# (parsed)";
        public IReadOnlyList<string> Extensions => ["cs"];
        public bool SeparatesDeclarationFromImplementation => false;
        public CandidateLines DeclarationCandidates => CandidateLines.All;

        public FilePosition Start { get; } = new ParsedPosition();

        public FilePosition After(FilePosition position, string line) => position;

        public Answer<Lexical> StateAt(FilePosition position, string line, int index) =>
            new(Lexical.Code, Evidence.Parsed);

        public Answer<Declared?> Declares(FilePosition position, string line) =>
            new(new Declared("Order", "Advance", DeclarationRole.Declaration), Evidence.Parsed);

        public bool HasImports => true;

        public ImportPathRules ImportPaths => ImportPathRules.AsWritten;

        public Answer<ImportsOnLine> ImportsOn(FilePosition position, string line) =>
            new(ImportsOnLine.Nothing, Evidence.Parsed);

        public Answer<bool> IsGenerated(string qualifiedPath) => new(false, Evidence.Parsed);

        public IReadOnlyList<Answer<ReferenceKind>> Occurrences(FilePosition position, string line, string symbol) =>
            [new Answer<ReferenceKind>(ReferenceKind.Definition, Evidence.Parsed)];

        /// <summary>
        ///     What a parser would carry between lines — a node, a scope — and what this one does not:
        ///     the point is that the type is the analyser's own, so a caller threading it through knows
        ///     nothing about what is in it.
        /// </summary>
        private sealed record ParsedPosition : FilePosition;
    }
}
