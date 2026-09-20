using System.Diagnostics;
using System.Diagnostics.Metrics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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

    /// <summary>
    ///     Which MCP tool was called. The one dimension a per-call duration is worth grouping by, and
    ///     the one #90 could only get by differencing transcript timestamps.
    /// </summary>
    public const string ToolTag = "codeexplorer.tool.name";

    public const string OutcomeTag = "codeexplorer.outcome";
    public const string FilesTag = "codeexplorer.files";
    public const string LinesTag = "codeexplorer.lines";
    public const string CommitsTag = "codeexplorer.commits";

    /// <summary>Which way the durable copy moved: <see cref="StoreOperation" /> or <see cref="FetchOperation" />.</summary>
    public const string DurableTag = "codeexplorer.index.durable.operation";

    public const string ToolDuration = "codeexplorer.tool.duration";
    public const string LeaseDuration = "codeexplorer.index.lease.duration";
    public const string SearchDuration = "codeexplorer.search.duration";
    public const string SearchFiles = "codeexplorer.search.files";
    public const string SearchLines = "codeexplorer.search.lines";
    public const string IndexDuration = "codeexplorer.index.build.duration";
    public const string IndexFiles = "codeexplorer.index.files";
    public const string IndexLines = "codeexplorer.index.lines";
    public const string HistoryDuration = "codeexplorer.index.history.duration";
    public const string HistoryCommits = "codeexplorer.index.history.commits";
    public const string HistoryFiles = "codeexplorer.index.history.files";
    public const string DurableDuration = "codeexplorer.index.durable.duration";

    // Span names are prefixed like the metric names, though only metrics and tags are held to it by
    // CODING_STANDARDS: a trace view shows these beside ASP.NET Core's own spans, and "search" alone
    // does not say whose.
    /// <summary>
    ///     One MCP tool call, from the moment the call-tool pipeline receives it to the moment it hands
    ///     a result back. The span #90 asked for: <see cref="SearchSpan" /> wraps the query, and the
    ///     query turned out to be the part that is nearly free — a glob returning 41 KB and a glob that
    ///     failed argument binding and ran no query at all cost the same 1.5 s. What dominates is
    ///     whatever this covers and that does not, so the pair of them is the measurement.
    /// </summary>
    public const string ToolSpan = "codeexplorer.tool";

    /// <summary>
    ///     Acquiring one lease on a project index: the restore if the disk lost it, the swap gate, the
    ///     <c>ATTACH</c>, the connection and the <c>USE</c>. The first candidate #90 names for the
    ///     floor, and a child of <see cref="ToolSpan" /> whenever a tool is what asked for it.
    /// </summary>
    public const string LeaseSpan = "codeexplorer.index.lease";

    public const string SearchSpan = "codeexplorer.search";
    public const string IndexSpan = "codeexplorer.index.build";

    /// <summary>
    ///     Separate from <see cref="IndexSpan" /> although both are one build: reading files and walking
    ///     history have different costs and different causes, and a build that got slow says nothing
    ///     about which of the two did. A first history walk is minutes where the file walk is seconds.
    /// </summary>
    public const string HistorySpan = "codeexplorer.index.history";

    public const string DurableSpan = "codeexplorer.index.durable";

    /// <summary>A search that reached an engine and got an answer, empty or not.</summary>
    public const string MatchedOutcome = "matched";

    /// <summary>A search that answered with an explanation: an empty query, a bad pattern, no index.</summary>
    public const string ProblemOutcome = "problem";

    /// <summary>The work threw. Recorded rather than dropped, so a failing project is visible as one.</summary>
    public const string FailedOutcome = "failed";

    /// <summary>
    ///     A tool call that returned a result. Not <see cref="MatchedOutcome" />: a tool that refuses a
    ///     path and a tool that answers with rows both returned, and from out here the difference is the
    ///     tool's business. What this dimension is for is telling a call that came back from one that
    ///     threw.
    /// </summary>
    public const string AnsweredOutcome = "answered";

    /// <summary>A lease that was granted. <see cref="AbsentOutcome" /> is a project with no index to lease.</summary>
    public const string OpenedOutcome = "opened";

    /// <summary>An index build that finished. A build has no second answer: it either completed or threw.</summary>
    public const string BuiltOutcome = "built";

    /// <summary>The durable copy moved: written to the store, or read back out of it.</summary>
    public const string MovedOutcome = "moved";

    /// <summary>
    ///     The store held no durable copy of this project, or held one an older build wrote. Neither is
    ///     a failure — both mean the index is rebuilt from git — and neither is a move, so a dashboard
    ///     that divides restores by wakes needs them apart.
    /// </summary>
    public const string AbsentOutcome = "absent";

    /// <summary>Writing the durable copy of a project index to the store.</summary>
    public const string StoreOperation = "store";

    /// <summary>Reading it back, which is what an off-hours wake pays for.</summary>
    public const string FetchOperation = "fetch";

    /// <summary>
    ///     The engine tag for a search that never reached one. A real engine name here would put a
    ///     made-up value on the dimension a dashboard groups by.
    /// </summary>
    public const string NoEngine = "none";

    private static readonly ActivitySource Source = new(ServiceName);
    private static readonly Meter Meter = new(ServiceName);

    // Seconds, which is what OTLP's semantic conventions use for a duration histogram; a backend's
    // default bucket boundaries assume it.
    private static readonly Histogram<double> ToolSeconds =
        Meter.CreateHistogram<double>(ToolDuration, "s", "How long one MCP tool call took inside the server.");

    private static readonly Histogram<double> LeaseSeconds =
        Meter.CreateHistogram<double>(LeaseDuration, "s", "How long acquiring one lease on a project index took.");

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

    private static readonly Histogram<double> HistorySeconds =
        Meter.CreateHistogram<double>(HistoryDuration, "s", "How long a history pass of a build took.");

    private static readonly Histogram<long> HistoryCommitCount =
        Meter.CreateHistogram<long>(HistoryCommits, "{commit}", "Commits appended by one history pass.");

    private static readonly Histogram<long> HistoryFileCount =
        Meter.CreateHistogram<long>(HistoryFiles, "{file}", "Files blamed by one history pass.");

    private static readonly Histogram<double> DurableSeconds =
        Meter.CreateHistogram<double>(DurableDuration, "s",
            "How long a project's durable copy took to store or to fetch.");

    /// <summary>
    ///     Where the OTLP exporter sends, and the switch that decides whether there is one at all: absent
    ///     means telemetry is off, which is what a plain <c>dotnet run</c> with an empty
    ///     <c>appsettings</c> gets (ADR-0004). The standard variable is honoured alongside the
    ///     configuration key so that the app and the exporter cannot disagree on whether it is on.
    /// </summary>
    public static Uri? OtlpEndpoint(IConfiguration configuration) =>
        Setting.Url(configuration, "Telemetry:OtlpEndpoint", Remedy)
        ?? Setting.Url(configuration, "OTEL_EXPORTER_OTLP_ENDPOINT", Remedy);

    private const string Remedy =
        "Give it one such as http://localhost:4317, or remove it to run without telemetry.";

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
    ///     What one search answer reached, for the two counts a matched search records. Zero files is
    ///     an answer and is recorded as one; a query with nothing to count — a page of the change log
    ///     counts commits and no lines — passes zero for what it does not have.
    /// </summary>
    public readonly record struct Measured(int Files, long Lines);

    /// <summary>
    ///     The chokepoint for one search: the envelope every search-shaped query used to write out for
    ///     itself. Open the recording, run the work, and record what came back as matched or as a
    ///     problem. It was twenty copies of those four lines across the history, file, import, grep,
    ///     reference, definition, match-list and declaration queries, and a copy is exactly how a new
    ///     query comes to record a different engine or to forget the problem call
    ///     (CODING_STANDARDS, Telemetry). <see cref="SearchRecording" /> is private to this class, so
    ///     there is no other way to record a search at all.
    ///     <typeparamref name="TAnswer" /> is the one result type the query answers with; anything else
    ///     is a <see cref="Problem" /> and recorded as one.
    /// </summary>
    /// <param name="slug">The project the query is for.</param>
    /// <param name="engine">What answered, for the engine tag.</param>
    /// <param name="work">The query itself.</param>
    /// <param name="measure">What the answer reached, for the file and line counts.</param>
    public static Task<Outcome> Search<TAnswer>(string slug, string engine, Func<Task<Outcome>> work,
        Func<TAnswer, Measured> measure) where TAnswer : Outcome =>
        Search(slug, _ => engine, work, measure);

    /// <summary>
    ///     The same where the answer is what decides which engine answered: grep picks full-text search
    ///     or a substring scan at query time, and the tag has to say which of the two it got.
    /// </summary>
    public static async Task<Outcome> Search<TAnswer>(string slug, Func<TAnswer, string> engine,
        Func<Task<Outcome>> work, Func<TAnswer, Measured> measure) where TAnswer : Outcome
    {
        using var recording = new SearchRecording(slug);
        var outcome = await work();
        if (outcome is not TAnswer answer)
        {
            recording.Problem();
            return outcome;
        }

        var (files, lines) = measure(answer);
        recording.Matched(engine(answer), files, lines);
        return outcome;
    }

    /// <summary>
    ///     The chokepoint for one lease on a project index, called from <c>ProjectIndexes</c> and
    ///     nowhere else. Inside the method that grants a lease rather than at its callers, so that every
    ///     way of asking for one is timed and a new caller cannot forget to be.
    /// </summary>
    public static LeaseRecording Lease(string slug) => new(slug);

    /// <summary>
    ///     Wraps the call-tool pipeline in a span and a duration, so that what an agent waits for is
    ///     measured where it happens rather than differenced out of a transcript afterwards (#90).
    ///     A filter and not an attribute on each tool: one registration covers every tool, including the
    ///     ones not written yet, and it is the outermost thing in the pipeline — so the gap between this
    ///     duration and the search duration inside it is exactly the unaccounted-for time the issue is
    ///     about, with no third measurement needed to find it.
    ///     A call with no project bound is not recorded at all. Every measurement here carries the slug
    ///     (CODING_STANDARDS, Telemetry), and an untagged one would be worth less than none; in practice
    ///     the transport binds one, because it is reached through <c>/projects/{project}/mcp</c>.
    ///     Telemetry never changes an answer: the result is returned and a throw rethrown exactly as
    ///     they arrived, and the recording is the only thing in between.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> ToolFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            if (BoundProject.SlugOrNull(request.Services) is not { } slug) return await next(request, cancellationToken);

            using var recording = new ToolRecording(slug, request.Params?.Name);
            var result = await next(request, cancellationToken);
            recording.Answered();
            return result;
        };

    /// <summary>The chokepoint for one index build, called from <c>IndexBuilder</c> and nowhere else.</summary>
    public static IndexBuildRecording IndexBuild(string slug) => new(slug);

    /// <summary>The chokepoint for the history pass of one build, called from <c>HistoryBuilder</c> and nowhere else.</summary>
    public static HistoryBuildRecording HistoryBuild(string slug) => new(slug);

    /// <summary>
    ///     The chokepoint for one move of a project's durable copy, called from <c>DurableIndex</c> and
    ///     nowhere else. The control database's backup deliberately has no recording: it belongs to no
    ///     project, and an untagged measurement is the one thing CODING_STANDARDS rules out.
    /// </summary>
    /// <param name="slug">The project whose durable copy is moving.</param>
    /// <param name="operation"><see cref="StoreOperation" /> or <see cref="FetchOperation" />.</param>
    public static DurableCopyRecording DurableCopy(string slug, string operation) => new(slug, operation);

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

        /// <summary>
        ///     The end of every recording, which was the same six lines in each of them: the duration,
        ///     tagged with the project, whatever dimension the recording owns and the outcome, and then
        ///     the span. Written once so that a seventh recording cannot record its duration under a
        ///     different tag set than the six before it.
        /// </summary>
        /// <param name="duration">The histogram this operation's seconds belong in.</param>
        /// <param name="outcome">How it ended.</param>
        /// <param name="own">The one dimension this recording adds of its own, before the outcome.</param>
        /// <param name="value">What that dimension was.</param>
        public void Complete(Histogram<double> duration, string outcome, string? own = null, object? value = null)
        {
            var tags = Tags;
            if (own is not null) tags.Add(own, value);
            tags.Add(OutcomeTag, outcome);
            duration.Record(Seconds, tags);
            Finish(outcome);
        }

        private void Finish(string outcome)
        {
            if (_activity is not { } activity) return;
            if (outcome == FailedOutcome) activity.SetStatus(ActivityStatusCode.Error);
            activity.SetTag(OutcomeTag, outcome);
            activity.Dispose();
        }
    }

    /// <summary>
    ///     One MCP tool call. Two outcomes only — it came back, or it threw — because what a tool made
    ///     of its arguments is the tool's own recording to make.
    /// </summary>
    public sealed class ToolRecording : IDisposable
    {
        private readonly Operation _operation;

        /// <summary>Left at <see cref="FailedOutcome" /> by a throw, which is the one path that sets nothing.</summary>
        private string _outcome = FailedOutcome;

        private readonly string _tool;

        internal ToolRecording(string slug, string? tool)
        {
            // A call the transport could not even name is still a call that cost time, and dropping it
            // would hide exactly the cheap-but-slow case #90 is chasing.
            _tool = string.IsNullOrEmpty(tool) ? "unknown" : tool;
            _operation = new Operation(ToolSpan, slug);
            _operation.Tag(ToolTag, _tool);
        }

        public void Dispose() => _operation.Complete(ToolSeconds, _outcome, ToolTag, _tool);

        public void Answered() => _outcome = AnsweredOutcome;
    }

    /// <summary>
    ///     One attempt to lease a project index. Timed whether or not there was an index to lease: a
    ///     project whose disk was wiped pays a restore here, and that is the expensive case worth seeing
    ///     apart from the cheap one rather than an absence of data.
    /// </summary>
    public sealed class LeaseRecording : IDisposable
    {
        private readonly Operation _operation;
        private string _outcome = FailedOutcome;

        internal LeaseRecording(string slug) => _operation = new Operation(LeaseSpan, slug);

        public void Dispose() => _operation.Complete(LeaseSeconds, _outcome);

        /// <summary>A lease was granted, and the caller now holds the project open against a swap.</summary>
        public void Opened() => _outcome = OpenedOutcome;

        /// <summary>There was no index to lease, which is an answer for the caller to phrase.</summary>
        public void Absent() => _outcome = AbsentOutcome;
    }

    /// <summary>
    ///     One search. <see cref="Matched" /> and <see cref="Problem" /> are the only two answers a
    ///     search has. Private, and opened only by <see cref="Search{TAnswer}(string,string,Func{Task{Outcome}},Func{TAnswer,Measured})" />:
    ///     the decision of which of the two an outcome is belongs to that one helper, so no query can
    ///     make it differently.
    /// </summary>
    private sealed class SearchRecording : IDisposable
    {
        private readonly Operation _operation;
        private string _engine = NoEngine;

        /// <summary>Left at <see cref="FailedOutcome" /> by a throw, which is the only path that records nothing else.</summary>
        private string _outcome = FailedOutcome;

        internal SearchRecording(string slug) => _operation = new Operation(SearchSpan, slug);

        public void Dispose() => _operation.Complete(SearchSeconds, _outcome, EngineTag, _engine);

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

        public void Dispose() => _operation.Complete(IndexSeconds, _outcome);

        public void Built(long files, long lines)
        {
            _outcome = BuiltOutcome;
            IndexFileCount.Record(files, _operation.Tags);
            IndexLineCount.Record(lines, _operation.Tags);
            _operation.Tag(FilesTag, files);
            _operation.Tag(LinesTag, lines);
        }
    }

    /// <summary>
    ///     The history pass of one build. Appending nothing and blaming nothing is the ordinary outcome
    ///     of refreshing a repository that did not change, and is recorded as a build rather than as an
    ///     absence: the useful question of this metric is how often a pass is the cheap kind.
    /// </summary>
    public sealed class HistoryBuildRecording : IDisposable
    {
        private readonly Operation _operation;
        private string _outcome = FailedOutcome;

        internal HistoryBuildRecording(string slug) => _operation = new Operation(HistorySpan, slug);

        public void Dispose() => _operation.Complete(HistorySeconds, _outcome);

        public void Built(long commits, long files)
        {
            _outcome = BuiltOutcome;
            HistoryCommitCount.Record(commits, _operation.Tags);
            HistoryFileCount.Record(files, _operation.Tags);
            _operation.Tag(CommitsTag, commits);
            _operation.Tag(FilesTag, files);
        }
    }

    /// <summary>
    ///     One move of a project's durable copy, in either direction. The duration is the whole of it:
    ///     storing is the <c>COPY TO</c> and the upload, and fetching is the download and the load,
    ///     which is why a fetch's recording is started before the transfer and carried on the copy
    ///     until it is loaded. That whole is what an off-hours wake waits for, and splitting it would
    ///     need a span per leg to be worth anything.
    /// </summary>
    public sealed class DurableCopyRecording : IDisposable
    {
        private readonly Operation _operation;
        private readonly string _which;
        private string _outcome = FailedOutcome;

        internal DurableCopyRecording(string slug, string operation)
        {
            _which = operation;
            _operation = new Operation(DurableSpan, slug);
            _operation.Tag(DurableTag, operation);
        }

        public void Dispose() => _operation.Complete(DurableSeconds, _outcome, DurableTag, _which);

        /// <summary>The copy was written, or read back and loaded.</summary>
        public void Moved() => _outcome = MovedOutcome;

        /// <summary>There was nothing to read back, or what there was an older schema wrote.</summary>
        public void Absent() => _outcome = AbsentOutcome;
    }
}
