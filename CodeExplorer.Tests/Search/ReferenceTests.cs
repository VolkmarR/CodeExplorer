using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The <c>find_references</c> tool over a project index (#11). The engine is pinned in every
///     test, as it is for grep: this tool never takes the BM25 path — a reference search is always
///     RE2 over each line — so the two engines must agree, and the shared tests run under both to
///     prove it rather than assume it.
/// </summary>
public sealed class ReferenceTests : IDisposable
{
    /// <summary>
    ///     One identifier in every shape the classifier has a name for: declared, written, called,
    ///     constructed, used as a type, read through a receiver, imported, quoted and commented.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> TwoRepositories = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] = """
                                using Domain.OrderStatus;

                                public class OrderService
                                {
                                    public OrderStatus Status { get; set; }

                                    public void Advance(OrderStatus next)
                                    {
                                        Status = next;
                                        var made = new OrderStatus();
                                        Log("OrderStatus");
                                        // OrderStatus in a comment
                                    }
                                }

                                """,
            ["src/Orders.g.cs"] = "public partial class OrderService\n{\n    public OrderStatus Generated;\n}\n"
        },
        ["two"] = new Dictionary<string, string>
        {
            ["lib/Report.cs"] = """
                                public class Report
                                {
                                    public void Run(OrderService service)
                                    {
                                        service.Advance(OrderStatus.New);
                                        var current = service.Status;
                                        service.Status = other.Status;
                                    }
                                }

                                """
        }
    };

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task Code_references_are_reported_apart_from_comments_strings_and_imports(SearchEngine engine)
    {
        await using var client = await StartAsync(engine);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderStatus" });

        // The import, the string and the comment are counted in the summary and kept out of the
        // listed sections; the five code references are what is shown.
        Assert.Contains("\"OrderStatus\" in 3 files: 5 references, 3 in comments, strings or imports", text);
        Assert.DoesNotContain("COMMENTS", text);
        Assert.Contains("TYPE USES", text);
        Assert.Contains("INSTANTIATIONS", text);
        Assert.Contains("one/src/Orders.cs", text);
        Assert.Contains("two/lib/Report.cs", text);
    }

    [Fact]
    public async Task Each_shape_of_reference_gets_its_own_section()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string status = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });
        Assert.Contains("DECLARATIONS", status);
        Assert.Contains("WRITES", status);
        Assert.Contains("READS", status);

        string advance = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Advance" });
        Assert.Contains("CALLS", advance);
        Assert.Contains("two/lib/Report.cs", advance);
        Assert.Contains("DECLARATIONS", advance);
    }

    [Fact]
    public async Task A_line_naming_it_twice_yields_a_reference_for_each()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });

        // `service.Status = other.Status;` is a write and a read. Classifying the line by its first
        // occurrence alone would report it as the write and lose the read entirely.
        Assert.Contains("2 writes", text);
        Assert.Contains("2 reads", text);
        // The line itself is printed once under each of the two sections it belongs to, not twice.
        Assert.Equal(2, Occurrences(text, "service.Status = other.Status;"));
    }

    private static int Occurrences(string text, string value)
    {
        int found = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            found++;
        return found;
    }

    [Fact]
    public async Task Results_carry_the_enclosing_type_and_member()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });

        // The write sits inside Advance, which sits inside OrderService; the property declaration has
        // no enclosing member, so it is labelled with the type alone.
        Assert.Contains("[OrderService.Advance] Status = next;", text);
        Assert.Contains("[OrderService] public OrderStatus Status", text);
        Assert.Contains("[Report.Run]", text);
    }

    [Fact]
    public async Task Writes_only_answers_what_changes_this()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "Status", ["writesOnly"] = true });

        Assert.Contains("WRITES", text);
        Assert.Contains("Status = next;", text);
        Assert.DoesNotContain("READS", text);
        Assert.DoesNotContain("CALLS", text);
    }

    [Fact]
    public async Task Noise_is_listed_only_when_asked_for()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["includeNoise"] = true });

        Assert.Contains("COMMENTS", text);
        Assert.Contains("STRINGS", text);
        Assert.Contains("IMPORTS", text);
        Assert.Contains("// OrderStatus in a comment", text);
    }

    [Fact]
    public async Task Filters_scope_the_search_and_a_hidden_file_is_named_as_hidden()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string scoped = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["repo"] = "two" });
        Assert.Contains("two/lib/Report.cs", scoped);
        Assert.DoesNotContain("one/src/Orders.cs", scoped);
        // Two of the three matching files were filtered away, and a declaration could be in either.
        Assert.Contains("your filters hid 2 further matching files", scoped);

        string excluded = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["exclude"] = "*.g.cs" });
        Assert.DoesNotContain("Orders.g.cs", excluded);
        Assert.Contains("your filters hid 1 further matching file", excluded);

        string byExtension = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["ext"] = "cs", ["path"] = "one/" });
        Assert.Contains("one/src/Orders.cs", byExtension);
        Assert.DoesNotContain("two/lib/Report.cs", byExtension);
    }

    /// <summary>
    ///     The count of files the filters hid comes out of the scan that found the references, not
    ///     out of a second regex pass over the whole of <c>lines</c> (#173). The count is asserted as
    ///     well as the plan, because one pass that answered a different number would be no saving.
    /// </summary>
    [Fact]
    public async Task A_filtered_search_scans_the_lines_once_for_the_references_and_the_hidden_count()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string plans = _host!.ScratchFile("reference-plans");
        string scoped;
        using (QueryPlan.Recording(plans))
            scoped = await FindAsync(client,
                new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["repo"] = "two" });

        Assert.Contains("your filters hid 2 further matching files", scoped);
        // Identified by the symbol bound into them, since the recording is process-wide: the other
        // searches for this name are in this class, whose tests run one at a time, and no other class
        // runs a reference search for it.
        string dump = Assert.Single(Directory.EnumerateFiles(plans, "*ReferenceSearch-QueryAsync.sql.txt"),
            file => File.ReadAllText(file).Contains("OrderStatus", StringComparison.Ordinal));
        // One statement is not yet one scan: a CTE the planner inlined into both of its readers would
        // read `lines` twice inside it. The profile is what says how often the table was read.
        using var profile = JsonDocument.Parse(File.ReadAllText(
            dump.Replace(".sql.txt", ".json", StringComparison.Ordinal)));
        Assert.Single(Operators(profile.RootElement), op =>
            op.GetProperty("operator_type").GetString() == "TABLE_SCAN"
            && op.GetProperty("extra_info").GetProperty("Table").GetString()!.EndsWith(".lines",
                StringComparison.Ordinal));
    }

    /// <summary>Every operator of a DuckDB JSON profile, depth first.</summary>
    private static IEnumerable<JsonElement> Operators(JsonElement node)
    {
        if (node.TryGetProperty("operator_type", out _)) yield return node;
        if (!node.TryGetProperty("children", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
        foreach (var op in Operators(child))
            yield return op;
    }

    [Fact]
    public async Task A_filtered_miss_is_told_apart_from_a_name_that_is_nowhere()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string hidden = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["ext"] = "md" });
        Assert.StartsWith("No references to", hidden);
        Assert.Contains("outside your filters", hidden);

        // Asserted as the whole sentence, not as an absence: a reply that stopped saying what it
        // searched would still hold "does not contain 'outside your'" (#87).
        string nowhere = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Unicorn" });
        Assert.StartsWith("No references to", nowhere);
        Assert.DoesNotContain("outside your", nowhere);
        Assert.Contains("Nothing in this project spells it; no filters narrowed the search, which spanned every file.",
            nowhere);
        Assert.Contains("matched whole and case-sensitively", nowhere);
    }

    [Fact]
    public async Task The_name_is_matched_whole_and_case_sensitively()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        // "Status" is a substring of "OrderStatus" and must not be found inside it; the counts below
        // are of the standalone references alone.
        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });
        Assert.DoesNotContain("new OrderStatus()", text);

        string wrongCase = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "status" });
        Assert.StartsWith("No references to", wrongCase);
    }

    [Fact]
    public async Task Every_reply_says_the_classification_is_textual()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderStatus" });

        Assert.Contains("Classification is textual", text);
        Assert.Contains("Strong evidence, not proof.", text);
    }

    [Fact]
    public async Task Malformed_input_is_explained_rather_than_answered_with_nothing()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string empty = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "  " });
        Assert.Contains("No symbol given", empty);

        string phrase = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "public OrderStatus" });
        Assert.Contains("is not one identifier", phrase);
        Assert.DoesNotContain("No references to", phrase);

        string punctuation = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "=>" });
        Assert.Contains("no identifier characters", punctuation);

        string unknownRepository = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["repo"] = "three" });
        Assert.Contains("No repository 'three'", unknownRepository);
        Assert.Contains("Repositories: one, two", unknownRepository);
    }

    [Fact]
    public async Task Only_the_files_naming_it_most_often_are_examined()
    {
        await using var client = await StartAsync(SearchEngine.Substring);

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "OrderStatus", ["maxFiles"] = 1 });

        Assert.Contains("3 files hold the name", text);
        Assert.Contains("Raise maxFiles", text);
    }

    [Fact]
    public async Task A_name_too_common_to_enumerate_is_cut_short_and_says_so()
    {
        _host = new TestHost(SearchEngine.Substring);
        string many = string.Concat(Enumerable.Range(0, ReferenceSearch.MaxLinesPerFile + 5)
            .Select(i => $"var x{i} = Widget;\n"));
        await _host.IndexedProjectAsync("beta",
            new Dictionary<string, Dictionary<string, string>>
                { ["one"] = new() { ["src/Many.cs"] = many } });
        await using var client = await _host.ConnectAsync("beta");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Widget" });

        // The lines beyond the ceiling are not read, and one of them could hold the declaration.
        Assert.Contains($"one file holds {ReferenceSearch.MaxLinesPerFile} or more lines naming it", text);
        Assert.Contains("was read no further", text);
        Assert.Contains("src/Many.cs", text);
    }

    /// <summary>
    ///     The visible outcome of the language seam (#57, ADR-0008): run find_references against an X#
    ///     file and writes appear. They never did before, because `:=` was not an assignment to
    ///     anything and `:` was not a receiver, so the one section that answers "what changes this?"
    ///     was always empty on a third of the codebases this server exists to serve.
    /// </summary>
    [Fact]
    public async Task An_XSharp_file_reports_its_writes_and_its_sends()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("xbase", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Orders.prg"] = """
                                     #using System.Collections

                                     class OrderService
                                     	method Advance(oNext as OrderStatus) as void
                                     		self:Status := oNext
                                     		local cLabel := self:Status
                                     		// Status in a comment
                                     	end method
                                     end class

                                     """
            }
        });
        await using var client = await _host.ConnectAsync("xbase");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Status" });

        Assert.Contains("1 write", text);
        Assert.Contains("WRITES", text);
        // The scope label proves the per-language declaration pattern reached DuckDB and came back:
        // `method Advance(` declares nothing a C# pattern would recognise.
        Assert.Contains("[OrderService.Advance] self:Status := oNext", text);
        // The send through `:` is a read and not an unplaced reference.
        Assert.Contains("1 read", text);
        Assert.Contains("READS", text);
        Assert.Contains("0 unplaced", text);
        // And the comment is still noise, counted rather than listed.
        Assert.Contains("2 references, 1 in comments, strings or imports", text);
    }

    /// <summary>
    ///     The other half of #83, over a real index: a named constant is a declaration (#71) and a
    ///     declaration is not automatically a scope. A local <c>const</c> sits above the lines that
    ///     follow it and is indented less than the ones inside the block below it, so before the two
    ///     modifier sets were split every reference under one was labelled with the constant instead of
    ///     with the method it sits in — the regression the X# profile had been keeping <c>define</c> out
    ///     to avoid, live in C# all along.
    /// </summary>
    [Fact]
    public async Task A_local_constant_is_not_the_scope_the_lines_below_it_are_labelled_with()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("limits", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Limits.cs"] = """
                                    public class Limits
                                    {
                                        public void Advance(int n)
                                        {
                                            const int Max = 10;
                                            if (n > 0)
                                            {
                                                Total = n + Max;
                                            }
                                        }

                                        public void Fill(int n)
                                        {
                                            readonly Span<int> s = stackalloc int[4];
                                            if (n > 0)
                                            {
                                                Total = s.Length;
                                            }
                                        }
                                    }

                                    """
            }
        });
        await using var client = await _host.ConnectAsync("limits");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Total" });

        Assert.DoesNotContain("Limits.Max", text);
        Assert.DoesNotContain("Limits.s", text);
        Assert.Contains("[Limits.Advance] Total = n + Max;", text);
        Assert.Contains("[Limits.Fill] Total = s.Length;", text);
    }

    /// <summary>
    ///     The file-level scan over a real index (#53). The lines a match sits on are the only ones
    ///     DuckDB hands back, so knowing that one of them is inside a block opened forty lines earlier
    ///     means reading the lines above it — which this proves happens, and happens per file.
    /// </summary>
    [Fact]
    public async Task A_commented_out_block_and_a_multi_line_literal_are_mentions_all_the_way_down()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("blocks", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Orders.cs"] = """
                                    public class OrderService
                                    {
                                        /* The old flow, kept for reference:
                                        Advance(next);
                                        var made = new Advance();
                                        */
                                        public const string Sql = @"
                                            select Advance from orders
                                            ";

                                        public void Run() => Advance(1);
                                    }

                                    """
            }
        });
        await using var client = await _host.ConnectAsync("blocks");

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "Advance", ["includeNoise"] = true });

        // One call, and it is the live one. The two lines inside the block comment used to be reported
        // as a call and an instantiation — deleted code under the headings an agent trusts most.
        Assert.Contains("1 call", text);
        Assert.Contains("=> Advance(1);", text);
        Assert.Contains("0 instantiations", text);
        // The commented-out lines and the line inside the verbatim literal are all mentions.
        Assert.Contains("3 in comments, strings or imports", text);
        Assert.Contains("select Advance from orders", text);
        Assert.Contains("0 unplaced", text);
    }

    /// <summary>
    ///     The project-wide occurrence total (#119). The detailed classification stops at the heaviest
    ///     files, and the sizing question — "how big is this change?" — is about all of them. The total
    ///     counts appearances and not lines, so a line naming the symbol twice counts twice.
    /// </summary>
    [Fact]
    public async Task A_sample_says_how_many_occurrences_the_whole_project_holds()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("wide", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Heavy.cs"] = "class Heavy\n{\n    void A() => Ship(Ship());\n    void B() => Ship();\n}\n",
                ["src/Thin.cs"] = "class Thin\n{\n    void C() => Ship();\n}\n",
                ["src/Other.cs"] = "class Other\n{\n    void D() => Ship();\n}\n"
            }
        });
        await using var client = await _host.ConnectAsync("wide");

        string text = await FindAsync(client,
            new Dictionary<string, object?> { ["symbol"] = "Ship", ["maxFiles"] = 1 });

        // The sample is the one heaviest file: three of the five occurrences.
        Assert.Contains("\"Ship\" in 1 file", text);
        Assert.Contains("3 calls", text);
        // The whole project is five, and the reply must not let the sample read as the total.
        Assert.Contains("a floor for the 1 files examined, not a project total", text);
        Assert.Contains("3 files hold the name, 5 occurrences in all", text);
        Assert.Contains("Raise maxFiles for the rest", text);
    }

    /// <summary>
    ///     The same note past the ceiling (#113). "Raise maxFiles" is arithmetic the caller cannot
    ///     complete once more files hold the name than maxFiles can ever examine, so the advice becomes
    ///     the pivot that does answer breadth.
    /// </summary>
    [Fact]
    public async Task Past_the_ceiling_the_note_names_a_pivot_instead_of_a_higher_cap()
    {
        _host = new TestHost(SearchEngine.Substring);
        var files = new Dictionary<string, string>();
        for (int i = 0; i <= ReferenceSearch.MaxFiles; i++)
            files[$"src/File{i}.cs"] = $"class File{i}\n{{\n    void Run() => Ship();\n}}\n";
        await _host.IndexedProjectAsync("huge",
            new Dictionary<string, Dictionary<string, string>> { ["one"] = files });
        await using var client = await _host.ConnectAsync("huge");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Ship" });

        Assert.Contains($"{ReferenceSearch.MaxFiles} is the most maxFiles can examine", text);
        Assert.Contains("grep(filesOnly=true) for breadth", text);
        Assert.DoesNotContain("Raise maxFiles", text);
        // The total is over every matching file and the sample stops at the cap, so on a fixture wider
        // than the cap the two numbers differ — which is the whole point of counting them apart (#119).
        int matching = ReferenceSearch.MaxFiles + 1;
        Assert.Contains($"{matching} files hold the name, {matching} occurrences in all", text);
        Assert.Contains($"a floor for the {ReferenceSearch.DefaultMaxFiles} files examined", text);
        Assert.Contains($"{ReferenceSearch.DefaultMaxFiles} calls", text);
    }

    /// <summary>
    ///     The same signal find_definition now carries (#126), from the other tool and with the other
    ///     consequence: a match in a file no profile covers is found like any other, and what it looks
    ///     like — a call, a write, a comment — was read from shapes that are not that language's.
    ///     An agent that trusted those labels would be trusting the weakest reading this produces.
    /// </summary>
    [Fact]
    public async Task References_in_a_file_no_profile_covers_are_named_as_read_with_default_shapes()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("mixed", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Ship.cs"] = "class Ship\n{\n    void Run() => Deliver();\n    void Deliver() { }\n}\n",
                ["lib/ship.rb"] = "def run\n  Deliver()\n  # Deliver is called above\nend\n",
                // Prose and project metadata name the symbol too, and neither is a file a language
                // profile would have read differently: a caveat about them would fire on every reply.
                ["README.md"] = "`Deliver` ships the order.\n",
                ["One.csproj"] = "<Project><Ship Include=\"Deliver\" /></Project>\n"
            }
        });
        await using var client = await _host.ConnectAsync("mixed");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Deliver" });

        Assert.Contains("no language profile covers", text);
        Assert.Contains(".rb", text);
        Assert.Contains("weaker evidence", text);
        // One file, not three: the Markdown and the project file spell the name and are not code this
        // failed to read. Counting them would put a caveat on nearly every reply this server gives.
        Assert.Contains("1 file spelling the name is .rb", text);
        // The note sits above the listing, where the reply cap cannot take it off the end.
        Assert.True(text.IndexOf("no language profile covers", StringComparison.Ordinal)
                    < text.IndexOf("CALLS", StringComparison.Ordinal));

        // A name that lives only in profiled files says nothing about profiles.
        string clean = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "Ship" });
        Assert.DoesNotContain("no language profile covers", clean);
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        string text = await FindAsync(client, new Dictionary<string, object?> { ["symbol"] = "OrderStatus" });

        Assert.Contains("no index to read from", text);
        Assert.Contains("POST /api/projects/alpha/refresh", text);
    }

    private async Task<McpClient> StartAsync(SearchEngine engine)
    {
        _host = new TestHost(engine);
        await _host.IndexedProjectAsync("alpha", TwoRepositories);
        return await _host.ConnectAsync("alpha");
    }

    private static Task<string> FindAsync(McpClient client, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, "find_references", arguments);
}
