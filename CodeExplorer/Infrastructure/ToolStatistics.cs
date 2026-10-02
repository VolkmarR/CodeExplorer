using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CodeExplorer.Infrastructure;

/// <summary>
///     One project's tool calls since this replica started, as the web UI's tool-call page shows them.
///     Nothing in it is persisted: a scale to zero starts it over, which is what <see cref="Since" />
///     is there to say.
/// </summary>
/// <param name="Since">When this replica began counting.</param>
/// <param name="CallsLastHour">The calls in <see cref="PerMinute" />, summed.</param>
/// <param name="FailedLastHour">Those of them that failed.</param>
/// <param name="P95Seconds">Over the last <see cref="ToolCallLog.RecentKept" /> calls; null before the first.</param>
/// <param name="Tools">Every tool called since <see cref="Since" />, most called first.</param>
/// <param name="PerMinute">The last sixty minutes, oldest first, with the empty ones included.</param>
/// <param name="Recent">The latest calls, newest first.</param>
public sealed record ToolCallStatistics(
    DateTimeOffset Since,
    int CallsLastHour,
    int FailedLastHour,
    double? P95Seconds,
    IReadOnlyList<ToolTotals> Tools,
    IReadOnlyList<MinuteCalls> PerMinute,
    IReadOnlyList<RecentCall> Recent);

/// <summary>
///     One tool's calls since the replica started. The percentiles are over its last
///     <see cref="ToolCallLog.DurationsPerTool" /> calls rather than all of them, so a tool that got slow
///     an hour ago reads slow now and not diluted by a morning of fast calls.
/// </summary>
public sealed record ToolTotals(
    string Tool,
    long Calls,
    long Failed,
    double P50Seconds,
    double P95Seconds,
    double MaxSeconds,
    DateTimeOffset LastCall);

public sealed record MinuteCalls(DateTimeOffset Minute, int Calls, int Failed);

/// <summary>One call of the recent list. <c>Id</c> is its place in its project's count, which the UI keys a row by.</summary>
public sealed record RecentCall(long Id, DateTimeOffset At, string Tool, double Seconds, bool Failed);

/// <summary>What the project list shows beside each project: whether agents are using it.</summary>
public sealed record ProjectToolActivity(string Project, int CallsLastHour, DateTimeOffset LastCall);

/// <summary>
///     Keeps the tool calls of every project in memory for the web UI, fed by the
///     <see cref="Telemetry.ToolDuration" /> histogram that <see cref="Telemetry.ToolFilter" /> records
///     into. It listens to that instrument rather than being called from the filter, so the page, the
///     OTLP export and the filter can never disagree on what a call was: there is one recording
///     (CODING_STANDARDS, Telemetry), and this is a second reader of it.
///     The listener is process-wide, as every <see cref="MeterListener" /> is. On the one replica the
///     application runs as that is the same thing as this server; a test process hosting several
///     servers sees each one's calls in every one of them, which is why the tests give each project a
///     slug of its own.
///     A hosted service so that it listens from startup: as a plain singleton it would only start
///     counting when the first page asked for the numbers.
/// </summary>
public sealed class ToolStatistics : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, ToolCallLog> _logs = new(StringComparer.Ordinal);
    private MeterListener? _listener;

    public DateTimeOffset Since { get; private set; } = DateTimeOffset.UtcNow;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Since = DateTimeOffset.UtcNow;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument is { Meter.Name: Telemetry.ServiceName, Name: Telemetry.ToolDuration })
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) => Record(seconds, tags));
        _listener.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>One project's calls. A project no agent has called yet answers with zeros, not a 404.</summary>
    public ToolCallStatistics For(string slug)
    {
        var now = DateTimeOffset.UtcNow;
        return _logs.TryGetValue(slug, out var log) ? log.Snapshot(now, Since) : ToolCallLog.Empty(now, Since);
    }

    /// <summary>Every project called since the replica started, for the project list to join by slug.</summary>
    public IReadOnlyList<ProjectToolActivity> Activity()
    {
        var now = DateTimeOffset.UtcNow;
        return [.. _logs.Select(pair => pair.Value.Activity(pair.Key, now))];
    }

    private void Record(double seconds, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? project = null, tool = null, outcome = null;
        foreach (var tag in tags)
            switch (tag.Key)
            {
                case Telemetry.ProjectTag: project = tag.Value as string; break;
                case Telemetry.ToolTag: tool = tag.Value as string; break;
                case Telemetry.OutcomeTag: outcome = tag.Value as string; break;
            }

        // The filter records no call without a project, so this is only ever a measurement someone
        // made by hand; with no slug there is no page to show it on.
        if (project is null) return;
        _logs.GetOrAdd(project, _ => new ToolCallLog())
            .Record(DateTimeOffset.UtcNow, tool ?? "unknown", seconds, outcome == Telemetry.FailedOutcome);
    }
}

/// <summary>
///     One project's calls, in fixed-size rings so a busy project costs the same memory as a quiet
///     one. Times are passed in rather than read, which is what lets a test say what minute it is.
/// </summary>
internal sealed class ToolCallLog
{
    /// <summary>The window the rate chart and the hour's totals cover.</summary>
    public const int MinutesKept = 60;

    /// <summary>What the project-wide p95 is over: enough for a stable tail, few enough to be recent.</summary>
    public const int RecentKept = 200;

    /// <summary>The page lists this many of the recent calls; more is a log nobody scrolls.</summary>
    public const int RecentShown = 50;

    /// <summary>What each tool's percentiles are over.</summary>
    public const int DurationsPerTool = 100;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, ToolTally> _tools = new(StringComparer.Ordinal);
    private readonly Bucket[] _minutes = new Bucket[MinutesKept];
    private readonly RecentCall[] _recent = new RecentCall[RecentKept];
    private int _recentCount;
    private DateTimeOffset _lastCall;

    public void Record(DateTimeOffset at, string tool, double seconds, bool failed)
    {
        long minute = MinuteOf(at);
        lock (_sync)
        {
            if (!_tools.TryGetValue(tool, out var tally)) _tools[tool] = tally = new ToolTally();
            tally.Add(at, seconds, failed);

            // A bucket still holding an older minute is one the ring has come round to: start it over.
            ref var bucket = ref _minutes[minute % MinutesKept];
            if (bucket.Minute != minute) bucket = new Bucket(minute, 0, 0);
            bucket = bucket with { Calls = bucket.Calls + 1, Failed = bucket.Failed + (failed ? 1 : 0) };

            _recent[_recentCount % RecentKept] = new RecentCall(_recentCount, at, tool, seconds, failed);
            _recentCount++;
            if (at > _lastCall) _lastCall = at;
        }
    }

    public ToolCallStatistics Snapshot(DateTimeOffset now, DateTimeOffset since)
    {
        lock (_sync)
        {
            var perMinute = PerMinute(now);
            var recent = Recent();
            return new ToolCallStatistics(since,
                perMinute.Sum(m => m.Calls),
                perMinute.Sum(m => m.Failed),
                recent.Count == 0 ? null : Percentile([.. recent.Select(c => c.Seconds)], 0.95),
                [
                    .. _tools.Select(pair => pair.Value.Totals(pair.Key))
                        .OrderByDescending(t => t.Calls).ThenBy(t => t.Tool, StringComparer.Ordinal)
                ],
                perMinute,
                [.. recent.Take(RecentShown)]);
        }
    }

    public ProjectToolActivity Activity(string slug, DateTimeOffset now)
    {
        lock (_sync) return new ProjectToolActivity(slug, PerMinute(now).Sum(m => m.Calls), _lastCall);
    }

    /// <summary>What a project with no calls yet answers with: the same shape, every count zero.</summary>
    public static ToolCallStatistics Empty(DateTimeOffset now, DateTimeOffset since) =>
        new ToolCallLog().Snapshot(now, since);

    private List<MinuteCalls> PerMinute(DateTimeOffset now)
    {
        long current = MinuteOf(now);
        var minutes = new List<MinuteCalls>(MinutesKept);
        for (long minute = current - MinutesKept + 1; minute <= current; minute++)
        {
            var bucket = _minutes[minute % MinutesKept];
            minutes.Add(bucket.Minute == minute
                ? new MinuteCalls(StartOf(minute), bucket.Calls, bucket.Failed)
                : new MinuteCalls(StartOf(minute), 0, 0));
        }

        return minutes;
    }

    /// <summary>Newest first.</summary>
    private List<RecentCall> Recent()
    {
        int kept = Math.Min(_recentCount, RecentKept);
        var calls = new List<RecentCall>(kept);
        for (int i = 1; i <= kept; i++) calls.Add(_recent[(_recentCount - i) % RecentKept]);
        return calls;
    }

    private static long MinuteOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / 60;

    private static DateTimeOffset StartOf(long minute) => DateTimeOffset.FromUnixTimeSeconds(minute * 60);

    /// <summary>Nearest rank, which reports a duration a call actually took rather than an interpolation.</summary>
    internal static double Percentile(double[] values, double p)
    {
        Array.Sort(values);
        int rank = (int)Math.Ceiling(p * values.Length);
        return values[Math.Clamp(rank - 1, 0, values.Length - 1)];
    }

    /// <summary>
    ///     One minute of the rate chart. <see cref="Minute" /> is minutes since the Unix epoch, and the
    ///     zero a fresh array holds is a minute in 1970 that no call is ever recorded in.
    /// </summary>
    private readonly record struct Bucket(long Minute, int Calls, int Failed);

    private sealed class ToolTally
    {
        private readonly double[] _durations = new double[DurationsPerTool];
        private long _calls;
        private long _failed;
        private double _max;
        private DateTimeOffset _last;

        public void Add(DateTimeOffset at, double seconds, bool failed)
        {
            _durations[_calls % DurationsPerTool] = seconds;
            _calls++;
            if (failed) _failed++;
            if (seconds > _max) _max = seconds;
            if (at > _last) _last = at;
        }

        public ToolTotals Totals(string tool)
        {
            double[] kept = _durations[..(int)Math.Min(_calls, DurationsPerTool)];
            return new ToolTotals(tool, _calls, _failed, Percentile([.. kept], 0.5), Percentile(kept, 0.95), _max,
                _last);
        }
    }
}
