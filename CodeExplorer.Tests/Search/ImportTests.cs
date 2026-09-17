using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The <c>imports</c> and <c>who_imports</c> tools over a project index (#55). The engine is
///     pinned as it is for the other tools; neither of these takes a text-search path at all — they
///     read a table the build filled — so one engine proves both, and the shared fixture runs under
///     both to say so rather than assume it.
/// </summary>
public sealed class ImportTests : IDisposable
{
    /// <summary>
    ///     A C# project where one namespace is declared by exactly one file and another by two, so
    ///     that a resolved edge and an ambiguous one are both in the same answer.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Sharp = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] = """
                                namespace Orders.Domain;

                                using System.Text;
                                using Orders.Storage;
                                // using Orders.Ghost;

                                public class OrderService;

                                """,
            ["src/Storage.cs"] = "namespace Orders.Storage;\n\npublic class Store;\n",
            ["src/Storage.Extra.cs"] = "namespace Orders.Storage;\n\npublic class Extra;\n",
            ["src/Report.cs"] = "namespace Orders.Reports;\n\nusing Orders.Domain;\n\npublic class Report;\n",
            ["db/install.sql"] = "create table orders (id integer);\n"
        }
    };

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task What_a_file_imports_is_answered_with_the_files_the_names_turned_out_to_be(
        SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string text = await ImportsAsync(client, "one/src/Orders.cs");

        Assert.Contains("one/src/Orders.cs (C#) imports 2 names", text);
        Assert.Contains("It declares itself as `Orders.Domain`", text);
        // `Orders.Storage` is declared by two files and therefore names neither of them.
        Assert.Contains("UNRESOLVED", text);
        Assert.Contains("Orders.Storage  -  several files declare this name", text);
        // Nothing in this project is System.Text, and saying so is different from leaving it out.
        Assert.Contains("System.Text  -  nothing in this project declares this name", text);
        // A commented-out import is not an import.
        Assert.DoesNotContain("Orders.Ghost", text);
        Assert.Contains("Strong evidence, not proof.", text);
    }

    [Fact]
    public async Task A_name_declared_by_exactly_one_file_resolves_to_it()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await ImportsAsync(client, "one/src/Report.cs");

        Assert.Contains("1 of them resolved to a file in this project", text);
        Assert.Contains("RESOLVED", text);
        Assert.Contains("Orders.Domain  ->  one/src/Orders.cs", text);
    }

    [Fact]
    public async Task The_reverse_direction_names_the_files_that_import_one()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await WhoImportsAsync(client, "one/src/Orders.cs");

        Assert.Contains("1 file imports one/src/Orders.cs", text);
        Assert.Contains("one/src/Report.cs:3  -  Orders.Domain", text);
    }

    /// <summary>
    ///     The case an empty list would read wrongly: two files declare the namespace, so an import of
    ///     it resolved to neither, and "nothing depends on this" would be a fact invented out of a
    ///     resolution this could not make.
    /// </summary>
    [Fact]
    public async Task A_file_sharing_its_namespace_is_told_why_the_reverse_lookup_is_thin()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await WhoImportsAsync(client, "one/src/Storage.cs");

        Assert.Contains("No file in this project was found to import one/src/Storage.cs", text);
        Assert.Contains("this file declares `Orders.Storage`, and 1 other file declares it too", text);
        Assert.Contains("grep for `Orders.Storage`", text);
    }

    [Fact]
    public async Task A_language_with_no_import_concept_is_told_apart_from_a_file_that_imports_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string sql = await ImportsAsync(client, "one/db/install.sql");
        Assert.Contains("is SQL, which has no import concept", sql);
        Assert.Contains("nothing was missed", sql);

        // A file in a language that has imports and writes none is the other answer entirely.
        string none = await ImportsAsync(client, "one/src/Storage.cs");
        Assert.Contains("imports 0 names", none);
        Assert.Contains("No import line was read in it", none);
    }

    [Fact]
    public async Task An_extension_no_profile_covers_says_so_rather_than_answering_with_nothing()
    {
        await using var client = await StartAsync("odd", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["build/notes.rst"] = ".. include:: other.rst\n" }
        });

        string text = await ImportsAsync(client, "one/build/notes.rst");

        Assert.Contains("no language profile covers that extension", text);
        Assert.Contains("different thing from it importing nothing", text);
    }

    /// <summary>
    ///     A relative path resolves against the importing file's own directory, with the extension
    ///     supplied the way the language would — which is the half of resolution a namespace lookup
    ///     cannot do.
    /// </summary>
    [Fact]
    public async Task A_relative_path_resolves_against_the_importing_files_directory()
    {
        await using var client = await StartAsync("web", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/app/main.ts"] = """
                                      import { render } from "./render";
                                      import { format } from "../lib/format";
                                      import React from "react";
                                      const late = await import("./render");

                                      """,
                ["src/app/render.ts"] = "export function render() {}\n",
                ["src/lib/format.ts"] = "export function format() {}\n",
                ["src/index.html"] = "<html><script src=\"app/main.js\"></script>\n"
                                     + "<link rel=\"stylesheet\" href=\"site.css\"></html>\n",
                ["src/site.css"] = "@import url(\"base.css\");\n.a { background: url(logo.png); }\n",
                ["src/base.css"] = ".b {}\n"
            }
        });

        string main = await ImportsAsync(client, "one/src/app/main.ts");
        Assert.Contains("./render  ->  one/src/app/render.ts", main);
        Assert.Contains("../lib/format  ->  one/src/lib/format.ts", main);
        // A package this project does not hold is a dependency all the same, and is reported as one
        // — not as a file missing from a directory it was never meant to be in, which is what would
        // send a reader looking for it.
        Assert.Contains("react  -  names a package, not a file in this project", main);

        // A dynamic import is the same edge, so the file is imported twice and both lines are named.
        string render = await WhoImportsAsync(client, "one/src/app/render.ts");
        Assert.Contains("one/src/app/main.ts:1", render);
        Assert.Contains("one/src/app/main.ts:4", render);

        string markup = await ImportsAsync(client, "one/src/index.html");
        Assert.Contains("site.css  ->  one/src/site.css", markup);

        string styles = await ImportsAsync(client, "one/src/site.css");
        Assert.Contains("base.css  ->  one/src/base.css", styles);
        Assert.Contains("logo.png", styles);
    }

    /// <summary>
    ///     The clause this ticket was written around: comma-separated, spanning lines, and written
    ///     once in each section of the unit. A unit is also the one language here whose module name
    ///     maps to exactly one file, so both directions are exact.
    /// </summary>
    [Fact]
    public async Task A_Delphi_uses_clause_is_read_whole_and_answers_both_directions()
    {
        await using var client = await StartAsync("delphi", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Main.pas"] = """
                                   unit Main;

                                   interface

                                   uses
                                     Customers, Orders,
                                     Invoices;

                                   implementation

                                   uses Logging;

                                   end.
                                   """,
                ["src/Customers.pas"] = "unit Customers;\ninterface\nimplementation\nend.\n",
                ["src/Orders.pas"] = "unit Orders;\ninterface\nimplementation\nend.\n",
                ["src/Invoices.pas"] = "unit Invoices;\ninterface\nimplementation\nend.\n",
                ["src/Logging.pas"] = "unit Logging;\ninterface\nimplementation\nend.\n"
            }
        });

        string text = await ImportsAsync(client, "one/src/Main.pas");

        // Four units over two clauses and three lines. A prefix read line by line finds Customers
        // and Logging and loses the other three.
        Assert.Contains("imports 4 names, 4 of them resolved", text);
        Assert.Contains("Customers  ->  one/src/Customers.pas", text);
        Assert.Contains("Orders  ->  one/src/Orders.pas", text);
        Assert.Contains("Invoices  ->  one/src/Invoices.pas", text);
        Assert.Contains("Logging  ->  one/src/Logging.pas", text);

        string invoices = await WhoImportsAsync(client, "one/src/Invoices.pas");
        Assert.Contains("1 file imports one/src/Invoices.pas", invoices);
        Assert.Contains("one/src/Main.pas:7", invoices);
    }

    [Fact]
    public async Task The_edges_survive_a_restore_from_the_durable_copy()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("alpha", Sharp);
        // What a scale to zero leaves behind: the index file gone and only the Parquet set left.
        _host.DeleteIndexFile("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        // A restored index that had lost the table would answer this with "no file imports it",
        // which reads as a fact about the code rather than about the restore.
        Assert.Contains("1 file imports one/src/Orders.cs", await WhoImportsAsync(client, "one/src/Orders.cs"));
    }

    [Fact]
    public async Task A_path_that_names_no_file_is_explained_rather_than_answered_with_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        Assert.Contains("No indexed file 'one/src/Nowhere.cs'", await ImportsAsync(client, "one/src/Nowhere.cs"));
        // The leaf name exists elsewhere, so the miss is a path that is wrong rather than a name
        // that is: an agent told only "no such file" would go looking for the file instead.
        Assert.Contains("Did you mean one/src/Orders.cs",
            await WhoImportsAsync(client, "one/elsewhere/Orders.cs"));
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        Assert.Contains("no index to read from", await ImportsAsync(client, "one/src/Orders.cs"));
    }

    private Task<McpClient> StartAsync(SearchEngine engine) => StartAsync("alpha", Sharp, engine);

    /// <summary>The host, the index and the client, which every test here wants and none varies.</summary>
    private async Task<McpClient> StartAsync(string slug,
        Dictionary<string, Dictionary<string, string>> files, SearchEngine engine = SearchEngine.Substring)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync(slug, files);
        return await _host.ConnectAsync(slug);
    }

    private static Task<string> ImportsAsync(McpClient client, string path) =>
        TestHost.CallAsync(client, "imports", new Dictionary<string, object?> { ["path"] = path });

    private static Task<string> WhoImportsAsync(McpClient client, string path) =>
        TestHost.CallAsync(client, "who_imports", new Dictionary<string, object?> { ["path"] = path });
}
