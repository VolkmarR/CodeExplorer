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
        Assert.Contains("Your filters hid 1 further matching file", hidden);
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
