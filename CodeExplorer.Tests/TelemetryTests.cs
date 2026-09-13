using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The spans and metrics search and indexing report (#10). Every assertion goes through a real
///     search or a real refresh rather than calling <see cref="Telemetry" /> directly: an instrument
///     nothing reaches is worth nothing, and the point of the ticket is that every entry point
///     reaches the same one.
///     Each test owns a project slug of its own. The instruments are process-wide and xunit runs test
///     classes in parallel, so the slug is what separates this test's telemetry from the suite's.
/// </summary>
public sealed class TelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Three files holding "Widget" on three lines, in two repositories.</summary>
    private static async Task<TestHost> ProjectAsync(SearchEngine engine, string slug)
    {
        var host = new TestHost(engine);
        try
        {
            await host.IndexedProjectAsync(slug, new Dictionary<string, Dictionary<string, string>>
            {
                ["one"] = new()
                {
                    ["src/Widget.cs"] = "class Widget\n{\n    int Size;\n}\n",
                    ["docs/Widget.md"] = "widget notes\n"
                },
                ["two"] = new() { ["src/Widget.cs"] = "class Widget { }\n" }
            });
            return host;
        }
        catch
        {
            // The host owns a data directory and an open DuckDB instance; a failure here would leak both.
            host.Dispose();
            throw;
        }
    }

    [Theory]
    [InlineData(SearchEngine.Substring, GrepSearch.SubstringEngine, "tele-search-substring")]
    [InlineData(SearchEngine.Fts, GrepSearch.FullTextEngine, "tele-search-fts")]
    public async Task A_search_records_its_duration_engine_and_result_counts(
        SearchEngine engine, string reported, string slug)
    {
        using var host = await ProjectAsync(engine, slug);
        using var probe = new TelemetryProbe(slug);

        var result = await SearchAsync(host, $"/api/projects/{slug}/search?q=Widget");
        Assert.Equal(3, result.TotalFiles);

        var duration = Assert.Single(probe.For(Telemetry.SearchDuration));
        Assert.Equal(reported, duration.Tags[Telemetry.EngineTag]);
        Assert.Equal(Telemetry.MatchedOutcome, duration.Tags[Telemetry.OutcomeTag]);
        Assert.True(duration.Value >= 0);

        Assert.Equal(3d, Assert.Single(probe.For(Telemetry.SearchFiles)).Value);
        Assert.Equal(3d, Assert.Single(probe.For(Telemetry.SearchLines)).Value);

        var span = probe.Span(Telemetry.SearchSpan);
        Assert.Equal(reported, span.GetTagItem(Telemetry.EngineTag));
        Assert.Equal(3, span.GetTagItem(Telemetry.FilesTag));
        Assert.Equal(3L, span.GetTagItem(Telemetry.LinesTag));
    }

    [Fact]
    public async Task A_second_search_entry_point_reports_the_same_attributes()
    {
        const string slug = "tele-tool";
        using var host = await ProjectAsync(SearchEngine.Substring, slug);

        using var probe = new TelemetryProbe(slug);
        await using (var client = await host.ConnectAsync(slug))
            await TestHost.CallAsync(client, "grep", new Dictionary<string, object?> { ["query"] = "Widget" });

        // The MCP tool and the JSON endpoint are the two entry points, and neither formats its own
        // telemetry: both go through GrepSearch, so the tag set cannot drift between them.
        var fromTool = Assert.Single(probe.For(Telemetry.SearchDuration));
        Assert.Equal(
            new[] { Telemetry.OutcomeTag, Telemetry.ProjectTag, Telemetry.EngineTag },
            fromTool.Tags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(GrepSearch.SubstringEngine, fromTool.Tags[Telemetry.EngineTag]);
        Assert.Equal(3d, Assert.Single(probe.For(Telemetry.SearchFiles)).Value);
    }

    /// <summary>
    ///     The chokepoint is the shape of the two services, not a convention: each exposes exactly one
    ///     public method, and that method records. A second entry point therefore has one thing to
    ///     call, and calling it is what reports the telemetry — there is no inner search or inner build
    ///     to reach past it. Asserted here because a new public method is the one edit that would
    ///     quietly open the way round.
    /// </summary>
    [Theory]
    [InlineData(typeof(GrepSearch), nameof(GrepSearch.SearchAsync))]
    [InlineData(typeof(IndexBuilder), nameof(IndexBuilder.FillAsync))]
    public void The_recorded_method_is_the_services_only_way_in(Type service, string only)
    {
        Assert.Equal([only],
            service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    ///     And the recording is started in that one method: the instruments are private to
    ///     <see cref="Telemetry" />, so this is about which file holds the call, which only the source
    ///     shows.
    /// </summary>
    [Theory]
    [InlineData("GrepSearch.cs", $"{nameof(Telemetry)}.{nameof(Telemetry.Search)}(")]
    [InlineData("IndexBuilder.cs", $"{nameof(Telemetry)}.{nameof(Telemetry.IndexBuild)}(")]
    public void Only_one_file_starts_each_recording(string file, string call) =>
        Assert.Equal([file], SourceFilesMentioning(call));

    [Fact]
    public async Task A_search_that_answers_with_a_problem_is_recorded_as_one()
    {
        const string slug = "tele-problem";
        using var host = await ProjectAsync(SearchEngine.Substring, slug);
        using var probe = new TelemetryProbe(slug);

        using var http = host.Factory.CreateClient();
        using var response = await http.GetAsync($"/api/projects/{slug}/search?q=Widget(&regex=true", Ct);
        Assert.False(response.IsSuccessStatusCode);

        var duration = Assert.Single(probe.For(Telemetry.SearchDuration));
        Assert.Equal(Telemetry.ProblemOutcome, duration.Tags[Telemetry.OutcomeTag]);
        // A malformed pattern never reached an engine, and reporting one would put a made-up value on
        // the dimension a dashboard groups by.
        Assert.Equal(Telemetry.NoEngine, duration.Tags[Telemetry.EngineTag]);
        // An absent measurement and a zero one mean different things: there was no count to report.
        Assert.Empty(probe.For(Telemetry.SearchFiles));
    }

    [Fact]
    public async Task A_search_that_throws_is_recorded_as_a_failure()
    {
        const string slug = "tele-failed";
        using var host = await ProjectAsync(SearchEngine.Substring, slug);
        using var probe = new TelemetryProbe(slug);

        // An agent that abandons a slow search is the real case; an already-cancelled token is the
        // same path, and it is the only way to make a search throw without a broken index.
        var search = host.Factory.Services.GetRequiredService<GrepSearch>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => search.SearchAsync(slug, new GrepRequest("Widget"), cancelled.Token));

        // A throw must not be dropped: a project failing every search would otherwise look idle.
        var duration = Assert.Single(probe.For(Telemetry.SearchDuration));
        Assert.Equal(Telemetry.FailedOutcome, duration.Tags[Telemetry.OutcomeTag]);
        Assert.Equal(ActivityStatusCode.Error, probe.Span(Telemetry.SearchSpan).Status);
    }

    [Fact]
    public async Task A_search_of_an_unknown_project_still_carries_its_slug()
    {
        using var host = await ProjectAsync(SearchEngine.Substring, "tele-known");
        using var probe = new TelemetryProbe("tele-ghost");

        using var http = host.Factory.CreateClient();
        using var response = await http.GetAsync("/api/projects/tele-ghost/search?q=Widget", Ct);
        Assert.False(response.IsSuccessStatusCode);

        // A project with no index is still a project someone searched, and which one is the whole
        // question an operator asks of it.
        Assert.Equal(Telemetry.ProblemOutcome,
            Assert.Single(probe.For(Telemetry.SearchDuration)).Tags[Telemetry.OutcomeTag]);
    }

    [Fact]
    public async Task An_index_build_records_its_duration_and_the_files_and_lines_it_read()
    {
        const string slug = "tele-build";
        using var host = new TestHost(SearchEngine.Substring);
        using var probe = new TelemetryProbe(slug);

        var summary = await host.IndexedProjectAsync(slug, new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/Widget.cs"] = "class Widget\n{\n}\n" },
            ["two"] = new() { ["lib/index.ts"] = "export const x = 1;\n" }
        });
        Assert.Equal(2, summary.Files);
        Assert.Equal(4, summary.Lines);

        Assert.Equal(Telemetry.BuiltOutcome,
            Assert.Single(probe.For(Telemetry.IndexDuration)).Tags[Telemetry.OutcomeTag]);
        Assert.Equal(2d, Assert.Single(probe.For(Telemetry.IndexFiles)).Value);
        Assert.Equal(4d, Assert.Single(probe.For(Telemetry.IndexLines)).Value);

        var span = probe.Span(Telemetry.IndexSpan);
        Assert.Equal(2L, span.GetTagItem(Telemetry.FilesTag));
        Assert.Equal(4L, span.GetTagItem(Telemetry.LinesTag));
    }

    [Fact]
    public async Task Every_span_and_metric_carries_the_project_slug()
    {
        const string slug = "tele-tagged";
        using var host = new TestHost(SearchEngine.Substring);
        using var probe = new TelemetryProbe(slug);

        await host.IndexedProjectAsync(slug,
            new Dictionary<string, Dictionary<string, string>> { ["one"] = new() { ["a.cs"] = "class A;\n" } });
        await SearchAsync(host, $"/api/projects/{slug}/search?q=class");

        // Asserted over everything this server emitted rather than instrument by instrument, which is
        // the shape of the rule: with several projects on one replica, untagged telemetry cannot
        // answer which department is slow. Other test classes emit into these lists too, and their
        // telemetry is under the same rule.
        Assert.NotEmpty(probe.For(Telemetry.SearchDuration));
        Assert.NotEmpty(probe.For(Telemetry.IndexDuration));
        Assert.All(probe.Measurements,
            m => Assert.False(string.IsNullOrEmpty(m.Tags.GetValueOrDefault(Telemetry.ProjectTag) as string)));
        Assert.All(probe.Activities,
            a => Assert.False(string.IsNullOrEmpty(a.GetTagItem(Telemetry.ProjectTag) as string)));
    }

    /// <summary>
    ///     "Switched off by configuration" is no OTLP endpoint configured, nothing more (#10): a plain
    ///     <c>dotnet run</c> with an empty <c>appsettings</c> exports nothing and still serves
    ///     (CODING_STANDARDS, Dependencies).
    /// </summary>
    [Fact]
    public void Telemetry_is_off_until_an_otlp_endpoint_is_configured()
    {
        Assert.Null(Telemetry.OtlpEndpoint(Configuration([])));
        Assert.Equal(new Uri("http://collector:4317"),
            Telemetry.OtlpEndpoint(Configuration(new Dictionary<string, string?>
                { ["Telemetry:OtlpEndpoint"] = "http://collector:4317" })));
        // The exporter reads the standard variable itself; honouring it here keeps the switch in one
        // place, so "is telemetry on" is not answered differently by the app and by the exporter.
        Assert.Equal(new Uri("http://standard:4317"),
            Telemetry.OtlpEndpoint(Configuration(new Dictionary<string, string?>
                { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://standard:4317" })));
        // A setting that is not a URL names itself rather than crashing with "Invalid URI".
        var bad = Assert.Throws<InvalidOperationException>(() =>
            Telemetry.OtlpEndpoint(Configuration(new Dictionary<string, string?>
                { ["Telemetry:OtlpEndpoint"] = "collector" })));
        Assert.Contains("Telemetry:OtlpEndpoint", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_serves_searches_with_no_telemetry_configured()
    {
        // The default TestHost sets no endpoint, so this is the offline default path, asserted rather
        // than assumed: a broken registration would otherwise fail every test at once and read as a
        // search defect.
        using var host = await ProjectAsync(SearchEngine.Substring, "tele-offline");
        Assert.Equal(3, (await SearchAsync(host, "/api/projects/tele-offline/search?q=Widget")).TotalFiles);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static async Task<GrepResult> SearchAsync(TestHost host, string url)
    {
        using var http = host.Factory.CreateClient();
        var result = await http.GetFromJsonAsync<GrepResult>(url, Ct);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    ///     The names of the server's source files whose code — comments and string literals removed —
    ///     contains the given text.
    /// </summary>
    private static List<string> SourceFilesMentioning(string text) =>
    [
        .. SourceTree.ServerFiles()
            .Where(file => SourceTree.Code(file).Contains(text, StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .Order(StringComparer.Ordinal)
    ];
}
