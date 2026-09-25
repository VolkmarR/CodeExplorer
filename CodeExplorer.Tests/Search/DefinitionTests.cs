using CodeExplorer.Index;
using CodeExplorer.Search;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The <c>find_definition</c> tool over a project index (#54). The engine is pinned in every test,
///     as it is for the other searches: this tool never takes the BM25 path — a declaration search is
///     always RE2 over each line — so the two engines must agree, and the shared test runs under both
///     to prove it rather than assume it.
/// </summary>
public sealed class DefinitionTests : IDisposable
{
    /// <summary>
    ///     A C# project where the same name is declared twice — a partial class and its generated half
    ///     — and used in a third file that declares nothing.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Sharp = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] = """
                                public class OrderService
                                {
                                    public void Advance(int n)
                                    {
                                        Log("Advance");
                                        // Advance is declared above
                                    }
                                }

                                """,
            ["src/Orders.Generated.cs"] = "public partial class OrderService\n{\n    public int Id;\n}\n",
            ["src/Report.cs"] = """
                                public class Report
                                {
                                    public void Run(OrderService service) => service.Advance(1);
                                }

                                """
        }
    };

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_declaration_is_answered_with_its_path_line_and_the_declaring_line(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Advance" });

        Assert.Contains("\"Advance\" is declared in 1 place.", text);
        Assert.Contains("one/src/Orders.cs", text);
        Assert.Contains("3: public void Advance(int n)", text);
        // The call, the string and the comment name it too and declare nothing.
        Assert.DoesNotContain("service.Advance(1)", text);
        Assert.DoesNotContain("Advance is declared above", text);
        // C# declares and implements in one place, so neither heading appears: a pair of them would
        // be a split invented for the sake of a heading.
        Assert.DoesNotContain("IMPLEMENTATIONS", text);
        Assert.DoesNotContain("DECLARATIONS", text);
        Assert.Contains("Strong evidence, not proof.", text);
    }

    [Fact]
    public async Task A_name_declared_in_several_places_returns_all_of_them()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderService" });

        // A partial class is declared twice, and answering with one of them is answering wrongly.
        Assert.Contains("\"OrderService\" is declared in 2 places.", text);
        Assert.Contains("one/src/Orders.cs", text);
        Assert.Contains("one/src/Orders.Generated.cs", text);
        // The file that only takes one as a parameter declares nothing.
        Assert.DoesNotContain("one/src/Report.cs", text);
    }

    /// <summary>
    ///     The reason this ticket was not a small one: Delphi announces a routine in the
    ///     <c>interface</c> section and writes it in <c>implementation</c>, the two lines are the same
    ///     line but for a qualifier, and the announcement is the one an agent wanted least.
    /// </summary>
    [Fact]
    public async Task A_Delphi_routine_lists_the_body_before_the_announcement()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("delphi", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Customers.pas"] = """
                                        unit Customers;

                                        interface

                                        type
                                          TCustomer = class(TObject)
                                            procedure Save;
                                          end;

                                        implementation

                                        procedure TCustomer.Save;
                                        begin
                                          Store(Self);
                                        end;

                                        procedure TCustomer.Reload;
                                        begin
                                        end;

                                        end.
                                        """
            }
        });
        await using var client = await _host.ConnectAsync("delphi");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Save" });

        Assert.Contains("\"Save\" is declared in 2 places.", text);
        Assert.Contains("This language announces a routine and writes it elsewhere", text);
        Assert.Contains("IMPLEMENTATIONS  (1)", text);
        Assert.Contains("DECLARATIONS  (1)", text);
        // The body comes first, and it is labelled with the type its qualified head names.
        Assert.True(text.IndexOf("procedure TCustomer.Save;", StringComparison.Ordinal)
                    < text.IndexOf("procedure Save;", StringComparison.Ordinal));
        Assert.Contains("[TCustomer] procedure TCustomer.Save;", text);

        // A routine written but never announced is still an implementation. Whether the label is
        // printed is a fact about Delphi and not about what this particular answer happened to hold,
        // so the heading is there for the one site as it is for two.
        string reload = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Reload" });
        Assert.Contains("declared in 1 place", reload);
        Assert.Contains("IMPLEMENTATIONS  (1)", reload);
        Assert.DoesNotContain("DECLARATIONS", reload);

        // And the class it belongs to is declared the way Delphi declares one, which no C-family
        // pattern reads.
        string type = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "TCustomer" });
        Assert.Contains("TCustomer = class(TObject)", type);
    }

    [Fact]
    public async Task A_PLSQL_spec_and_body_are_told_apart_across_two_files()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("oracle", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["db/pkg_orders.pks"] = """
                                        create or replace package pkg_orders as
                                          procedure add_order(p_id number);
                                        end pkg_orders;
                                        """,
                ["db/pkg_orders.pkb"] = """
                                        create or replace package body pkg_orders as
                                          procedure add_order(p_id number) is
                                          begin
                                            null;
                                          end add_order;
                                        end pkg_orders;
                                        """
            }
        });
        await using var client = await _host.ConnectAsync("oracle");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "add_order" });

        // Neither file mentions the other; the header line of each is what says which half it is.
        Assert.Contains("IMPLEMENTATIONS  (1)", text);
        Assert.Contains("one/db/pkg_orders.pkb", text);
        Assert.Contains("DECLARATIONS  (1)", text);
        Assert.Contains("one/db/pkg_orders.pks", text);
        Assert.True(text.IndexOf("pkg_orders.pkb", StringComparison.Ordinal)
                    < text.IndexOf("pkg_orders.pks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sql_script_holding_both_halves_is_told_apart_by_its_headers()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("script", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                // The `.sql` extension says which dialect to read this as and nothing about which half
                // of a package a line is in, which is why the headers carry the roles.
                ["db/install.sql"] = """
                                     create or replace package pkg_orders as
                                       procedure add_order(p_id number);
                                     end pkg_orders;
                                     /
                                     create or replace package body pkg_orders as
                                       procedure add_order(p_id number) is
                                       begin
                                         null;
                                       end add_order;
                                     end pkg_orders;
                                     """
            }
        });
        await using var client = await _host.ConnectAsync("script");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "add_order" });

        Assert.Contains("IMPLEMENTATIONS  (1)", text);
        Assert.Contains("DECLARATIONS  (1)", text);
        Assert.True(text.IndexOf("6: procedure add_order", StringComparison.Ordinal)
                    < text.IndexOf("2: procedure add_order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_XSharp_declaration_forms_are_read()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("xbase", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Orders.prg"] = """
                                     class OrderService
                                     	method Advance(n as int) as void
                                     		Store(self)
                                     	end method
                                     	access Status as string
                                     	assign Status(value as string)
                                     end class

                                     function Start() as void
                                     	OrderService{}:Advance(1)

                                     """
            }
        });
        await using var client = await _host.ConnectAsync("xbase");

        Assert.Contains("method Advance(n as int) as void",
            await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Advance" }));
        Assert.Contains("class OrderService",
            await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderService" }));
        Assert.Contains("function Start() as void",
            await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Start" }));

        // `access` and `assign` are two halves of one property and two declarations of one name.
        string status = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });
        Assert.Contains("access Status as string", status);
        Assert.Contains("assign Status(value as string)", status);
    }

    /// <summary>
    ///     A member named with a letter outside ASCII (#235), which German and Italian X# code is full of.
    ///     The declaration shapes the engine narrows candidates with read a name as RE2's ASCII-only
    ///     <c>\w</c>, so <c>Größe</c> was never a candidate line and the member was never declared.
    /// </summary>
    [Fact]
    public async Task A_member_named_with_letters_outside_ascii_is_declared()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("umlaut", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Lager.cs"] = "public class Lager\n{\n    public int Größe;\n}\n",
                ["src/Lager.prg"] = """
                                    class Regal
                                    	method Größe() as int
                                    		return 1
                                    	end method
                                    end class

                                    """
            }
        });
        await using var client = await _host.ConnectAsync("umlaut");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Größe" });
        Assert.Contains("\"Größe\" is declared in 2 places.", text);
        Assert.Contains("public int Größe;", text);
        Assert.Contains("method Größe() as int", text);
    }

    [Fact]
    public async Task A_symbol_with_no_recognised_declaration_is_pointed_at_the_search_instead()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        // `Log` is called and never declared here, which is the common case for a symbol declared in a
        // dependency or in a form no profile reads. An empty list would read as "there is none".
        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Log" });
        Assert.Contains("No declaration of \"Log\" was recognised", text);
        Assert.Contains("the name appears in 1 file", text);
        Assert.Contains("find_references(symbol=\"Log\")", text);
        Assert.Contains("not proof there is none", text);

        // A name that is nowhere at all is a different answer, and says so.
        string nowhere = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Unicorn" });
        Assert.Contains("Nothing in this project spells \"Unicorn\"", nowhere);
        Assert.Contains("matched whole and case-sensitively", nowhere);
    }

    [Fact]
    public async Task Filters_scope_the_search_and_say_what_they_hid()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string scoped = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderService", ["exclude"] = "*.Generated.cs" });
        Assert.Contains("\"OrderService\" is declared in 1 place.", scoped);
        Assert.DoesNotContain("Orders.Generated.cs", scoped);

        // A declaration hidden by a filter reads exactly like one that does not exist, so the reply
        // has to name the files the filter took away.
        string hidden = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "Advance", ["path"] = "src/Report" });
        Assert.Contains("No declaration of \"Advance\" was recognised", hidden);
        Assert.Contains("your filters hid 1 further matching file", hidden);
    }

    /// <summary>
    ///     An extension no profile covers is named outright (#126), the way imports and
    ///     list_declarations already name one. Those files are searched with the conservative default
    ///     shapes, so a declaration written the way that language writes one is missing from the answer
    ///     — and a reply that said nothing about it would read the same as one that searched them.
    /// </summary>
    [Fact]
    public async Task An_extension_no_profile_covers_is_named_rather_than_silently_contributing_nothing()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("mixed", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Orders.cs"] = "public class OrderService\n{\n    public void Advance(int n) { }\n}\n",
                // Ruby declares a routine in a form no profile here knows, so the declaration on this
                // line is exactly what the answer cannot see.
                ["lib/orders.rb"] = "class Orders\n  def Advance(n)\n    n\n  end\nend\n"
            }
        });
        await using var client = await _host.ConnectAsync("mixed");

        string found = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Advance" });

        // The C# declaration is answered, and the reply still says what it could not read.
        Assert.Contains("one/src/Orders.cs", found);
        Assert.Contains("no language profile covers", found);
        Assert.Contains(".rb", found);
        Assert.Contains("1 file spelling the name is", found);

        // A scope of nothing but unprofiled files is the sharpest case: an empty answer there is not
        // a negative, and the reply has to say which of the two it is.
        string only = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "Advance", ["ext"] = "rb" });
        Assert.Contains("No declaration of \"Advance\" was recognised", only);
        Assert.Contains("no language profile covers", only);
        Assert.Contains("silent about those files rather than negative about them", only);

        // And a search that met no unprofiled file says nothing about one: a note on every reply is
        // one an agent stops reading.
        string profiled = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderService" });
        Assert.DoesNotContain("no language profile covers", profiled);
    }

    /// <summary>
    ///     The other half of the same silence (#129): a language that has a profile but declares no
    ///     declaration shapes contributes no branch to the candidate query, so not one of its lines is
    ///     read — strictly less than the unprofiled case, which at least gets the default shapes. It
    ///     earns its own sentence rather than #126's, which would promise shapes that never ran.
    /// </summary>
    [Fact]
    public async Task A_profiled_language_with_no_declaration_shapes_says_nothing_was_scanned_there()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("markup", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Menu.cs"] = "public class Menu\n{\n    public void Open() { }\n}\n",
                // HTML has a profile — comments, strings, imports — and no declaration shapes at all,
                // so these two functions are invisible to the scan in a way a .rb file's is not. The
                // second shares its name with the C# method, which is what puts the note on a reply
                // that found something.
                ["www/menu.html"] =
                    "<script>\n  function toggleMenu(open) { return open; }\n  function Open() { }\n</script>\n",
                // And an unprofiled language spelling the same name, so one reply has both reasons to
                // report and has to keep them apart.
                ["lib/menu.rb"] = "class Menu\n  def toggleMenu(n)\n    n\n  end\nend\n"
            }
        });
        await using var client = await _host.ConnectAsync("markup");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "toggleMenu" });

        Assert.Contains("No declaration of \"toggleMenu\" was recognised", text);
        // Both reasons, each with its own clause — #126's says those files were read with the default
        // shapes, and saying that of a file no shape ever reached would be the same silence in a more
        // confident voice — and one consequence, because what to do about either is the same thing.
        Assert.Contains("1 file spelling the name is .rb, which no language profile covers", text);
        Assert.Contains("1 further file spelling the name is HTML", text);
        Assert.Contains("whose declarations are not something that can be read from a line", text);
        Assert.Contains("nothing in it was scanned", text);
        Assert.Equal(1, text.Split("NOTE:").Length - 1);

        // Alone, the unreadable clause opens the note and counts from itself: "further" is only true
        // of a clause that follows one.
        string markup = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "toggleMenu", ["ext"] = "html" });
        Assert.Contains("1 file spelling the name is HTML", markup);
        Assert.DoesNotContain("no language profile covers", markup);

        // An answer that found a declaration is where the note is likeliest to be read as the whole
        // of what there is, so it carries the same sentence — the HTML `Open` is not in the list.
        string found = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Open" });
        Assert.Contains("\"Open\" is declared in 1 place.", found);
        Assert.Contains("one/src/Menu.cs", found);
        Assert.Contains("whose declarations are not something that can be read from a line", found);

        // find_references classifies an occurrence with the analyser whatever the language, so a
        // shapeless profile costs it nothing and it says nothing about one (#129).
        string references = await TestHost.CallAsync(client, "find_references",
            new Dictionary<string, object?> { ["symbol"] = "toggleMenu" });
        Assert.Contains("www/menu.html", references);
        Assert.DoesNotContain("whose declarations are not something that can be read from a line",
            references);

        // A search that meets only profiled, readable files carries no note at all: a caveat on every
        // reply is one an agent stops reading.
        string clean = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "Open", ["ext"] = "cs" });
        Assert.DoesNotContain("NOTE:", clean);
    }

    [Fact]
    public async Task Malformed_input_is_explained_rather_than_answered_with_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        Assert.Contains("No symbol given",
            await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "  " }));
        Assert.Contains("is not one identifier",
            await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "public OrderService" }));
        Assert.Contains("no identifier characters",
            await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "=>" }));
        Assert.Contains("No repository 'three'",
            await FindAsync(client,
                new Dictionary<string, object?> { ["symbol"] = "OrderService", ["repo"] = "three" }));
    }

    [Fact]
    public async Task A_declaration_commented_out_or_quoted_is_not_a_declaration()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("prose", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Old.cs"] = """
                                 public class Keeper
                                 {
                                     /* The old shape, kept for reference:
                                     public void Advance(int n)
                                     */
                                     public const string Sql = @"
                                         public void Advance(int n)
                                         ";
                                 }

                                 """
            }
        });
        await using var client = await _host.ConnectAsync("prose");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Advance" });

        // Both lines are shaped exactly like a declaration; only the lines above them say they are not
        // one. Reported as declarations, they are deleted code under the heading an agent trusts most.
        Assert.Contains("No declaration of \"Advance\" was recognised", text);
    }

    /// <summary>
    ///     What #83 was opened for: an X# file that is nothing but named constants. Three such files in
    ///     AcsLib answered that they declared nothing, because `define` introduces a name and opens no
    ///     scope and one modifier list had to answer both questions. The fixture is what can be checked
    ///     here; the three real files are a re-index and a look.
    /// </summary>
    [Fact]
    public async Task A_file_of_nothing_but_XSharp_defines_declares_every_one_of_them()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("xbase", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Fsedit.vh"] = """
                                    define FSEDIT_GET := 11
                                    define PD_ALLPAGES             := 0x00000000

                                    """
            }
        });
        await using var client = await _host.ConnectAsync("xbase");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "FSEDIT_GET" });

        Assert.Contains("\"FSEDIT_GET\" is declared in 1 place.", text);
        Assert.Contains("one/src/Fsedit.vh", text);
        Assert.Contains("1: define FSEDIT_GET := 11", text);
    }

    /// <summary>
    ///     #239: a line shaped like a declaration that only mentions the name — here as a parameter
    ///     type — declares another name, and more than the candidate cap of them sorting first used to
    ///     crowd the real declaration out of the lines read at all.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task Lines_that_only_mention_the_name_do_not_crowd_out_its_declaration(SearchEngine engine)
    {
        _host = new TestHost(engine);
        string uses = string.Concat(Enumerable.Range(0, DefinitionSearch.MaxCandidates + 100)
            .Select(i => $"    public void Run{i}(OrderService s) {{ }}\n"));
        await _host.IndexedProjectAsync("crowd", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["a/Uses.cs"] = $"public class Uses\n{{\n{uses}}}\n",
                ["z/OrderService.cs"] = "public class OrderService {}\n"
            }
        });
        await using var client = await _host.ConnectAsync("crowd");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderService" });

        Assert.Contains("\"OrderService\" is declared in 1 place.", text);
        Assert.Contains("one/z/OrderService.cs", text);
        Assert.DoesNotContain("candidate lines", text);
    }

    /// <summary>
    ///     #239: where more lines than the cap really are shaped like a declaration of the name, the
    ///     ones past it are never placed, and the reply says so rather than reading as the whole answer.
    /// </summary>
    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_search_that_reaches_the_candidate_cap_says_so(SearchEngine engine)
    {
        _host = new TestHost(engine);
        string overloads = string.Concat(Enumerable.Range(0, DefinitionSearch.MaxCandidates + 1)
            .Select(i => $"    public void Advance(int n{i}) {{ }}\n"));
        await _host.IndexedProjectAsync("capped", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/Overloads.cs"] = $"public class Overloads\n{{\n{overloads}}}\n" }
        });
        await using var client = await _host.ConnectAsync("capped");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Advance" });

        Assert.Contains($"more than {DefinitionSearch.MaxCandidates} candidate lines", text);
        Assert.Contains("may be incomplete", text);
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderService" });

        Assert.Contains("no index to read from", text);
        Assert.Contains("POST /api/projects/alpha/refresh", text);
    }

    private async Task<McpClient> StartAsync(SearchEngine engine)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync("alpha", Sharp);
        return await _host.ConnectAsync("alpha");
    }

    private static Task<string> FindAsync(McpClient client, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, "find_definition", arguments);
}
