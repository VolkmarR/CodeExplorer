using System.Text.Json;
using System.Text.Json.Nodes;
using CodeExplorer.Index;
using CodeExplorer.Language;
using CodeExplorer.Reading;
using CodeExplorer.Refresh;
using CodeExplorer.Search;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The JSON the web UI compares against, read as text: the property names and every enum value
///     the browser holds a string union for. Read raw rather than through the server's records,
///     because a typed read accepts either spelling of an enum value and would pass whichever one the
///     server wrote — and the spelling is the contract, since the web's unions are written by hand.
///     At the root of the tests and not in a module's folder, because what it pins is the one
///     serializer setting in <c>Program.cs</c> that every module's endpoints answer through.
/// </summary>
public sealed class HttpJsonTests(HttpJsonFixture fixture) : IClassFixture<HttpJsonFixture>
{
    private readonly TestHost _host = fixture.Host;

    /// <summary>
    ///     Every member of every enum an HTTP answer carries, spelled through the server's HTTP options.
    ///     The endpoint tests below can reach only the members their fixture produces; this is the
    ///     whole list the unions in <c>web/src</c> are written against, and the import shape the web
    ///     does not read yet.
    /// </summary>
    [Fact]
    public void Every_enum_the_api_sends_goes_out_by_its_member_name()
    {
        Assert.Equal(["NeverRun", "Queued", "Running", "Succeeded", "Failed"], Spelled<RefreshState>());
        Assert.Equal(["GitAttributes", "WellKnownName", "History"], Spelled<SuggestionRule>());
        Assert.Equal(["Day", "Week", "Month"], Spelled<ChangePeriod>());
        Assert.Equal(["Unprofiled", "Unreadable", "Read"], Spelled<DeclarationCoverage>());
        Assert.Equal(["Declaration", "Implementation"], Spelled<DeclarationRole>());
        Assert.Equal(["Text", "Parsed"], Spelled<Evidence>());
        Assert.Equal(["Module", "Path"], Spelled<ImportShape>());
    }

    /// <summary>A value that is no member fails on the server rather than reaching the web as a number.</summary>
    [Fact]
    public void A_value_that_is_no_member_is_refused_rather_than_sent_as_a_number() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((Evidence)7, _host.HttpJsonOptions));

    private List<string> Spelled<T>() where T : struct, Enum =>
        [.. Enum.GetValues<T>().Select(value => JsonSerializer.Serialize(value, _host.HttpJsonOptions).Trim('"'))];

    [Fact]
    public async Task A_refresh_status_names_its_state()
    {
        var never = await _host.GetJsonNodeAsync($"/api/projects/{HttpJsonFixture.Unbuilt}/refresh");
        Assert.Equal(["project", "state", "phase", "startedAt", "finishedAt", "summary", "error", "progress", "phases"],
            Names(never));
        Assert.Equal("NeverRun", (string?)never["state"]);

        var built = await _host.GetJsonNodeAsync($"/api/projects/{HttpJsonFixture.Built}/refresh");
        Assert.Equal("Succeeded", (string?)built["state"]);
    }

    [Fact]
    public async Task A_suggested_exclusion_names_the_rule_that_proposed_it()
    {
        var detail = await _host.GetJsonNodeAsync($"/api/projects/{HttpJsonFixture.Built}/excluded-paths/suggestions");

        var suggestion = Assert.Single(detail["suggestions"]!.AsArray(),
            s => (string?)s!["pattern"] == "one/Generated/**");
        Assert.Equal(
            """{"pattern":"one/Generated/**","rule":"GitAttributes","reason":"linguist-generated in one/.gitattributes","files":1}""",
            suggestion!.ToJsonString());
    }

    [Fact]
    public async Task The_files_added_and_deleted_card_names_its_period()
    {
        var detail = await _host.GetJsonNodeAsync($"/api/projects/{HttpJsonFixture.Built}/overview");

        var changes = detail["cards"]!["fileChanges"]!;
        Assert.Equal(["period", "periods"], Names(changes));
        Assert.Equal("Week", (string?)changes["period"]);
    }

    [Fact]
    public async Task A_repository_names_its_newest_commit()
    {
        var detail = await _host.GetJsonNodeAsync($"/api/projects/{HttpJsonFixture.Built}");

        var repository = Assert.Single(detail["repositories"]!.AsArray())!;
        Assert.Equal(["sha", "authorName", "authoredAt", "subject"], Names(repository["newestCommit"]!));
    }

    [Fact]
    public async Task An_import_edge_is_answered_with_what_the_rail_draws()
    {
        var imports = await _host.GetJsonNodeAsync(Route("imports", "one/src/Orders.cs"));

        Assert.Equal(["qualifiedPath", "languageName", "profiled", "hasImports", "module", "capped", "imports"],
            Names(imports));
        var edge = Assert.Single(imports["imports"]!.AsArray())!;
        Assert.Equal(["name", "shape", "lineNumber", "targetPath", "unresolved", "evidence"], Names(edge));
        Assert.Equal("System.Text", (string?)edge["name"]);
        Assert.Equal("Module", (string?)edge["shape"]);
        Assert.Equal("Text", (string?)edge["evidence"]);
    }

    [Fact]
    public async Task A_declaration_names_its_role_and_evidence()
    {
        var declarations = await _host.GetJsonNodeAsync(Route("declarations", "one/src/Customers.pas"));

        Assert.Equal(["qualifiedPath", "languageName", "coverage", "capped", "offset", "declarations"], Names(declarations));
        Assert.Equal("Read", (string?)declarations["coverage"]);
        Assert.Equal(
            [
                """{"lineNumber":6,"text":"  TCustomer = class(TObject)","type":"TCustomer","member":null,"role":"Declaration","evidence":"Text"}""",
                """{"lineNumber":7,"text":"    procedure Save;","type":null,"member":"Save","role":"Declaration","evidence":"Text"}""",
                """{"lineNumber":12,"text":"procedure TCustomer.Save;","type":"TCustomer","member":"Save","role":"Implementation","evidence":"Text"}"""
            ],
            declarations["declarations"]!.AsArray().Select(d => d!.ToJsonString()));
    }

    /// <summary>The two coverages besides <c>Read</c>, which an empty declarations panel tells apart.</summary>
    [Theory]
    [InlineData("one/web/site.css", "Unreadable")]
    [InlineData("one/build/notes.rst", "Unprofiled")]
    public async Task An_empty_declaration_list_names_its_coverage(string path, string coverage)
    {
        var declarations = await _host.GetJsonNodeAsync(Route("declarations", path));

        Assert.Equal(coverage, (string?)declarations["coverage"]);
    }

    private static string Route(string direction, string path) =>
        $"/api/projects/{HttpJsonFixture.Built}/file/{direction}?path={Uri.EscapeDataString(path)}";

    private static List<string> Names(JsonNode node) => [.. node.AsObject().Select(p => p.Key)];
}

/// <summary>
///     One built project holding a file for every enum the web compares against, and one project never
///     refreshed, whose status is the idle one. Built once, because every test here only reads.
/// </summary>
public sealed class HttpJsonFixture : IAsyncLifetime
{
    public const string Built = "built";

    public const string Unbuilt = "unbuilt";

    public TestHost Host { get; } = new(SearchEngine.Substring);

    public async ValueTask InitializeAsync()
    {
        await Host.IndexedProjectAsync(Built, new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                [".gitattributes"] = "Generated/** linguist-generated\n",
                ["Generated/A.cs"] = "class A { }\n",
                ["src/Orders.cs"] = "namespace Orders.Domain;\n\nusing System.Text;\n\npublic class OrderService;\n",
                // A Delphi unit, because its interface and implementation sections are what give a
                // declaration a role at all.
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
                                        end;

                                        end.

                                        """,
                ["web/site.css"] = ".panel { color: red; }\n",
                ["build/notes.rst"] = "nothing here\n",
                ["vendor/lib.js"] = "var lib = 1;\n"
            }
        });
        await Host.CreateProjectAsync(Unbuilt);

        // For ApiContractTests, whose snapshot sees a shape only where the answer holds one: an
        // excluded path set after the build, so the overview has excluded files to count, and a tool
        // call, so the call statistics have a row. The statistics are process-wide (ToolStatistics),
        // so without one of its own the project-less list would be empty or not depending on which
        // other tests ran first.
        await Host.SetExcludedPathsAsync(Built, ["one/vendor/**"]);
        await using var client = await Host.ConnectAsync(Built);
        await TestHost.CallAsync(client, "which_project", []);
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        return ValueTask.CompletedTask;
    }
}
