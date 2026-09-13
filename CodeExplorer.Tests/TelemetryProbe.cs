using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodeExplorer.Tests;

/// <summary>One recorded measurement: the instrument that took it, its value, and the tags it carried.</summary>
public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
///     Listens to the app's <see cref="ActivitySource" /> and <see cref="Meter" /> for the duration of a
///     test. It is what an OTLP exporter is in production: the instruments are inert until something
///     listens, so a test that asserts on them has to be that something. Only this service's own
///     source and meter are subscribed, so ASP.NET Core's own instrumentation stays out of the lists.
///     A listener is process-wide and xunit runs test classes in parallel, so the probe is built
///     around one project slug: <see cref="For" /> and <see cref="Span" /> see that project alone,
///     while <see cref="Measurements" /> and <see cref="Activities" /> keep everything, which is what
///     an assertion about the tags every project's telemetry carries needs.
/// </summary>
public sealed class TelemetryProbe : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly List<Activity> _activityList = [];
    private readonly Lock _sync = new();
    private readonly MeterListener _meters;
    private readonly List<Measurement> _measurementList = [];
    private readonly string _slug;

    /// <param name="slug">The project whose telemetry this test is about.</param>
    public TelemetryProbe(string slug)
    {
        _slug = slug;
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.ServiceName,
            // Recorded and not Propagate: the test reads the tags, which are only kept on a sampled activity.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_sync) _activityList.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(_activities);

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.ServiceName) listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
    }

    /// <summary>Spans that have finished. A span still open when a test reads this is deliberately absent.</summary>
    public IReadOnlyList<Activity> Activities
    {
        get
        {
            lock (_sync) return [.. _activityList];
        }
    }

    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_sync) return [.. _measurementList];
        }
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags) copy[tag.Key] = tag.Value;
        lock (_sync) _measurementList.Add(new Measurement(instrument.Name, value, copy));
    }

    /// <summary>What this probe's project recorded on one instrument.</summary>
    public IReadOnlyList<Measurement> For(string instrument) =>
    [
        .. Measurements.Where(m =>
            m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault(Telemetry.ProjectTag), _slug))
    ];

    /// <summary>This probe's project's one span of that name.</summary>
    public Activity Span(string name) =>
        Activities.Single(a => a.OperationName == name && Equals(a.GetTagItem(Telemetry.ProjectTag), _slug));
}
