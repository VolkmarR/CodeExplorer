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

    /// <summary>The first appearance of the symbol on the line, as this analyser places it.</summary>
    private static ReferenceKind Kind(ILanguageAnalyzer analyzer, string line, string symbol)
    {
        var placed = analyzer.Occurrences(line, symbol);
        Assert.NotEmpty(placed);
        return placed[0].Value;
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
    [InlineData("prg", "#region MAX_ORDERS", "MAX_ORDERS")]
    [InlineData("cs", "#if MAX_ORDERS", "MAX_ORDERS")]
    [InlineData("cs", "#region MAX_ORDERS", "MAX_ORDERS")]
    public void A_leading_hash_is_a_directive_and_not_a_comment(string extension, string line, string symbol) =>
        Assert.NotEqual(ReferenceKind.Comment, Kind(extension, line, symbol));

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
        Assert.Equal(symbol, Languages.Default.For(extension).Declares(line).Value?.Member);
    }

    [Fact]
    public void A_wrapped_boolean_clause_is_not_a_declaration() =>
        // `or` on its own would make the continuation line of any WHERE clause a declaration, and
        // DECLARATIONS is the section an agent trusts most.
        Assert.NotEqual(ReferenceKind.Definition, Kind("sql", "   or status = 1", "status"));

    [Fact]
    public void A_local_does_not_become_the_scope_everything_under_it_is_labelled_with()
    {
        // `local`, `instance`, `var` and `const` introduce a name and not a scope. With them in the
        // modifier list every reference below one was labelled with the local instead of the method.
        Assert.Null(Languages.Default.For("prg").Declares("	local cLabel := self:Status").Value);
        Assert.Null(Languages.Default.For("pas").Declares("  var Total: Integer;").Value);
    }

    [Fact]
    public void A_call_inside_a_template_literal_is_still_a_call() =>
        // A backtick delimiter that does not know `${…}` is code turns every call made inside one
        // into a string mention, which loses real calls. Interpolated literals are #53's.
        Assert.Equal(ReferenceKind.Call, Kind("ts", "const label = `a ${advance(1)} b`;", "advance"));

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
        int calls = analyzer.Occurrences(line, "advance").Count(k => k.Value == ReferenceKind.Call);
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
        var registry = Languages.Default.With(new TextAnalyzer(invented));

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
        Assert.Equal(Evidence.Parsed, analyzer.Occurrences("Status = next;", "Status")[0].Evidence);
        Assert.Equal(ReferenceKind.Definition, Kind(analyzer, "Status = next;", "Status"));
        // The text analyser is still there for everything the stub did not claim — including `.csx`,
        // which the C# profile claims and the stub does not.
        Assert.Equal("X#", registry.For("prg").Language);
        Assert.Equal("C#", registry.For("csx").Language);
        Assert.Equal(Evidence.Text, registry.For("prg").Occurrences("oOrder:Status := 1", "Status")[0].Evidence);
    }

    [Fact]
    public void The_last_registration_wins_however_many_there_have_been()
    {
        // The overlay claims `cs` while the C# profile also claims `csx`, so both stay in the map.
        // Recovering the order from the map rather than keeping it let the loser take `cs` back.
        var registry = Languages.Default.With(new StubAnalyzer());
        for (int i = 0; i < 6; i++)
            registry = registry.With(new TextAnalyzer(new LanguageProfile($"Filler{i}", [$"f{i}"])));

        Assert.Equal("C# (parsed)", registry.For("cs").Language);
        Assert.Equal("C#", registry.For("csx").Language);
        // And re-registering the text profile afterwards takes it back, which is what order means.
        Assert.Equal("C#", registry.With(Languages.Default.For("csx")).For("cs").Language);
    }

    [Fact]
    public void Every_answer_says_how_it_was_reached()
    {
        var analyzer = Languages.Default.For("cs");
        Assert.Equal(Evidence.Text, analyzer.StateAt("// x", 3).Evidence);
        Assert.Equal(Evidence.Text, analyzer.Declares("public class Order").Evidence);
        Assert.Equal(Evidence.Text, analyzer.ImportOn("using System;").Evidence);
        Assert.Equal(Evidence.Text, analyzer.IsGenerated("src/Order.g.cs").Evidence);
        Assert.Equal(Evidence.Text, analyzer.Occurrences("Order x;", "Order")[0].Evidence);
    }

    [Fact]
    public void An_import_line_is_told_apart_from_what_it_imports()
    {
        Assert.Equal("System.Text", Languages.Default.For("cs").ImportOn("using System.Text;").Value);
        Assert.Equal("System.Collections", Languages.Default.For("prg").ImportOn("#using System.Collections").Value);
        Assert.Equal("Vcl.Forms, Vcl.Dialogs", Languages.Default.For("pas").ImportOn("uses Vcl.Forms, Vcl.Dialogs;").Value);
        Assert.Null(Languages.Default.For("cs").ImportOn("        Status = next;").Value);
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
        Assert.Equal("Advance", Languages.Default.For("cs").Declares("    public void Advance(int n)").Value?.Member);
        Assert.Equal("OrderService", Languages.Default.For("cs").Declares("public class OrderService").Value?.Type);
        // The name follows the introducing word here, with the return type after it rather than before.
        Assert.Equal("Advance", Languages.Default.For("prg").Declares("method Advance(n as int) as void").Value?.Member);
        Assert.Equal("Advance", Languages.Default.For("pas").Declares("procedure Advance(n: Integer);").Value?.Member);
        Assert.Equal("advance", Languages.Default.For("sql").Declares("create or replace function advance(n int)").Value?.Member);
        Assert.Null(Languages.Default.For("cs").Declares("        Status = next;").Value);
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

        public Answer<Lexical> StateAt(string line, int index) => new(Lexical.Code, Evidence.Parsed);

        public Answer<Declared?> Declares(string line) =>
            new(new Declared("Order", "Advance", DeclarationRole.Declaration), Evidence.Parsed);

        public Answer<string?> ImportOn(string line) => new(null, Evidence.Parsed);

        public Answer<bool> IsGenerated(string qualifiedPath) => new(false, Evidence.Parsed);

        public IReadOnlyList<Answer<ReferenceKind>> Occurrences(string line, string symbol) =>
            [new Answer<ReferenceKind>(ReferenceKind.Definition, Evidence.Parsed)];
    }
}
