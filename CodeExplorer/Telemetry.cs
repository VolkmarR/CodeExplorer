using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodeExplorer;

/// <summary>
///     Every instrument this server owns, and the two recordings that write to them. It is one class
///     and not one per module, because the tag set is the thing worth keeping identical and a
///     recording split across folders drifts.
///     Nothing here needs telemetry to be switched on: an <see cref="ActivitySource" /> with no
///     listener returns a null activity and a <see cref="Meter" /> with no listener discards a
///     measurement, so the whole path costs a few nanoseconds when no OTLP endpoint is configured.
/// </summary>
public static class Telemetry
{
    /// <summary>Names the <see cref="ActivitySource" />, the <see cref="Meter" /> and the OTLP resource alike.</summary>
    public const string ServiceName = "CodeExplorer";

    /// <summary>
    ///     Which project the work was for. On every span and every metric without exception: several
    ///     projects share a replica, and untagged telemetry cannot answer which department is slow.
    /// </summary>
    public const string ProjectTag = "codeexplorer.project";

    /// <summary>Which engine answered a search, or <see cref="NoEngine" /> when none was reached.</summary>
    public const string EngineTag = "codeexplorer.search.engine";

    public const string OutcomeTag = "codeexplorer.outcome";
    public const string FilesTag = "codeexplorer.files";
    public const string LinesTag = "codeexplorer.lines";

    public const string SearchDuration = "codeexplorer.search.duration";
    public const string SearchFiles = "codeexplorer.search.files";
    public const string SearchLines = "codeexplorer.search.lines";
    public const string IndexDuration = "codeexplorer.index.build.duration";
    public const string IndexFiles = "codeexplorer.index.files";
    public const string IndexLines = "codeexplorer.index.lines";

    // Span names are prefixed like the metric names, though only metrics and tags are held to it by
    // CODING_STANDARDS: a trace view shows these beside ASP.NET Core's own spans, and "search" alone
    // does not say whose.
    public const string SearchSpan = "codeexplorer.search";
    public const string IndexSpan = "codeexplorer.index.build";

    /// <summary>A search that reached an engine and got an answer, empty or not.</summary>
    public const string MatchedOutcome = "matched";

    /// <summary>A search that answered with an explanation: an empty query, a bad pattern, no index.</summary>
    public const string ProblemOutcome = "problem";

    /// <summary>The work threw. Recorded rather than dropped, so a failing project is visible as one.</summary>
    public const string FailedOutcome = "failed";

    /// <summary>An index build that finished. A build has no second answer: it either completed or threw.</summary>
    public const string BuiltOutcome = "built";

    /// <summary>
    ///     The engine tag for a search that never reached one. A real engine name here would put a
    ///     made-up value on the dimension a dashboard groups by.
    /// </summary>
    public const string NoEngine = "none";

    private static readonly ActivitySource Source = new(ServiceName);
    private static readonly Meter Meter = new(ServiceName);

    // Seconds, which is what OTLP's semantic conventions use for a duration histogram; a backend's
    // default bucket boundaries assume it.
    private static readonly Histogram<double> SearchSeconds =
        Meter.CreateHistogram<double>(SearchDuration, "s", "How long a search took, end to end.");

    private static readonly Histogram<long> SearchFileCount =
        Meter.CreateHistogram<long>(SearchFiles, "{file}", "Files matched by one search, across every repository.");

    private static readonly Histogram<long> SearchLineCount =
        Meter.CreateHistogram<long>(SearchLines, "{line}", "Lines matched by one search.");

    private static readonly Histogram<double> IndexSeconds =
        Meter.CreateHistogram<double>(IndexDuration, "s", "How long an index build took.");

    private static readonly Histogram<long> IndexFileCount =
        Meter.CreateHistogram<long>(IndexFiles, "{file}", "Files read into an index by one build.");

    private static readonly Histogram<long> IndexLineCount =
        Meter.CreateHistogram<long>(IndexLines, "{line}", "Lines read into an index by one build.");

    /// <summary>
    ///     Where the OTLP exporter sends, and the switch that decides whether there is one at all: absent
    ///     means telemetry is off, which is what a plain <c>dotnet run</c> with an empty
    ///     <c>appsettings</c> gets (ADR-0004). The standard variable is honoured alongside the
    ///     configuration key so that the app and the exporter cannot disagree on whether it is on.
    /// </summary>
    public static Uri? OtlpEndpoint(IConfiguration configuration)
    {
        string? value = configuration["Telemetry:OtlpEndpoint"] ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Named rather than left to UriFormatException: the server refuses to start over this, and
        // "Invalid URI: The format of the URI could not be determined" does not say which setting.
        // InvalidOperationException and not McpException: no MCP tool is on this path, startup is.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException(
                $"Telemetry:OtlpEndpoint (or OTEL_EXPORTER_OTLP_ENDPOINT) is '{value}', which is not an absolute "
                + "URL. Give it one such as http://localhost:4317, or remove it to run without telemetry.");
        return endpoint;
    }

    /// <summary>
    ///     Registers tracing and metrics when an OTLP endpoint is configured, and does nothing at all
    ///     when it is not: no exporter, no background flush, and no instrumentation listening.
    ///     The ASP.NET Core, HTTP client and runtime instrumentation registered alongside is the
    ///     library's own and carries no project tag; the slug rule is about what this server records,
    ///     and a request span covers a route that may name no project at all.
    /// </summary>
    public static void AddTelemetry(this WebApplicationBuilder builder)
    {
        if (OtlpEndpoint(builder.Configuration) is not { } endpoint) return;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing
                .AddSource(ServiceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options => options.Endpoint = endpoint))
            .WithMetrics(metrics => metrics
                .AddMeter(ServiceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(options => options.Endpoint = endpoint));
    }

    /// <summary>
    ///     The chokepoint for one search. Called from <c>GrepSearch</c> and nowhere else, so a new
    ///     entry point reports the same attributes by having no way to report different ones: the
    ///     instruments above are private, so nothing outside this file can record a search at all.
    /// </summary>
    public static SearchRecording Search(string slug) => new(slug);

    /// <summary>The chokepoint for one index build, called from <c>IndexBuilder</c> and nowhere else.</summary>
    public static IndexBuildRecording IndexBuild(string slug) => new(slug);

    /// <summary>
    ///     One operation in flight: its span, how long it has been running, and the project tag both
    ///     ends of the recording start from. It is composed into the recordings rather than inherited
    ///     by them, because an abstract base with a public <c>Dispose</c> owes every derived type a
    ///     finaliser contract (CA1816) that nothing here has any use for.
    ///     The elapsed time is measured from a timestamp rather than read off the activity, because
    ///     there is no activity at all when nothing is listening.
    /// </summary>
    private readonly struct Operation(string name, string slug)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly Activity? _activity = StartTagged(name, slug);

        public double Seconds => Stopwatch.GetElapsedTime(_started).TotalSeconds;

        /// <summary>A fresh tag set carrying the project, which every measurement of this operation adds to.</summary>
        public TagList Tags => new() { { ProjectTag, slug } };

        public void Tag(string tag, object? value) => _activity?.SetTag(tag, value);

        private static Activity? StartTagged(string name, string slug)
        {
            var activity = Source.StartActivity(name, ActivityKind.Internal);
            activity?.SetTag(ProjectTag, slug);
            return activity;
        }

        public void Finish(string outcome)
        {
            if (_activity is not { } activity) return;
            if (outcome == FailedOutcome) activity.SetStatus(ActivityStatusCode.Error);
            activity.SetTag(OutcomeTag, outcome);
            activity.Dispose();
        }
    }

    /// <summary>One search. <see cref="Matched" /> and <see cref="Problem" /> are the only two answers a search has.</summary>
    public sealed class SearchRecording : IDisposable
    {
        private readonly Operation _operation;
        private string _engine = NoEngine;

        /// <summary>Left at <see cref="FailedOutcome" /> by a throw, which is the only path that records nothing else.</summary>
        private string _outcome = FailedOutcome;

        internal SearchRecording(string slug) => _operation = new Operation(SearchSpan, slug);

        public void Dispose()
        {
            var tags = _operation.Tags;
            tags.Add(EngineTag, _engine);
            tags.Add(OutcomeTag, _outcome);
            SearchSeconds.Record(_operation.Seconds, tags);
            _operation.Finish(_outcome);
        }

        /// <summary>A search that reached an engine. Zero files is an answer and is recorded as one.</summary>
        public void Matched(string engine, int files, long lines)
        {
            _engine = engine;
            _outcome = MatchedOutcome;
            var tags = _operation.Tags;
            tags.Add(EngineTag, engine);
            SearchFileCount.Record(files, tags);
            SearchLineCount.Record(lines, tags);
            _operation.Tag(FilesTag, files);
            _operation.Tag(LinesTag, lines);
            _operation.Tag(EngineTag, engine);
        }

        /// <summary>
        ///     A search that answered with an explanation. No result counts: an absent measurement and a
        ///     zero one mean different things, and a problem produced no count to report.
        /// </summary>
        public void Problem()
        {
            _outcome = ProblemOutcome;
            _operation.Tag(EngineTag, NoEngine);
        }
    }

    /// <summary>One index build, from the first blob read to the last.</summary>
    public sealed class IndexBuildRecording : IDisposable
    {
        private readonly Operation _operation;
        private string _outcome = FailedOutcome;

        internal IndexBuildRecording(string slug) => _operation = new Operation(IndexSpan, slug);

        public void Dispose()
        {
            var tags = _operation.Tags;
            tags.Add(OutcomeTag, _outcome);
            IndexSeconds.Record(_operation.Seconds, tags);
            _operation.Finish(_outcome);
        }

        public void Built(long files, long lines)
        {
            _outcome = BuiltOutcome;
            IndexFileCount.Record(files, _operation.Tags);
            IndexLineCount.Record(lines, _operation.Tags);
            _operation.Tag(FilesTag, files);
            _operation.Tag(LinesTag, lines);
        }
    }
}
