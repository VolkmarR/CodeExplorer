using Microsoft.Extensions.Logging;

namespace CodeExplorer.Tests;

/// <summary>One log entry as a test reads it: how bad it was, and what it said once formatted.</summary>
public sealed record LogEntry(LogLevel Level, string Message);

/// <summary>
///     Captures what a host logged, for the few places where the log line is the product rather than a
///     trace of it: an operator reading "local only" at start is how a deployment missing its key ring
///     configuration is caught at all, so it is asserted like any other behaviour.
///     It is a provider and not a logger, because that is what a host hands its categories to; every
///     category is captured, and the test filters.
/// </summary>
public sealed class LogProbe : ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _sync = new();

    public ILogger CreateLogger(string categoryName) => new ProbeLogger(this);

    public void Dispose()
    {
    }

    /// <summary>The one entry at this level whose message contains the text, and a failure if there is none.</summary>
    public LogEntry Only(LogLevel level, string containing)
    {
        lock (_sync)
            return _entries.Single(entry => entry.Level == level
                                            && entry.Message.Contains(containing, StringComparison.Ordinal));
    }

    private void Add(LogEntry entry)
    {
        lock (_sync) _entries.Add(entry);
    }

    private sealed class ProbeLogger(LogProbe probe) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Everything is enabled: a test that asserts on an Information line must not depend on the
        // host's configured minimum, and the guards in the app are about cost rather than filtering.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            probe.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
