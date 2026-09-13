using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer;

/// <summary>
///     Where a project's refresh stands. Serialised by name rather than by ordinal, so the web UI
///     compares against a word and inserting a state later does not silently re-label the others.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RefreshState>))]
public enum RefreshState
{
    /// <summary>No refresh has been asked for since this replica started. The index may still be built (#9).</summary>
    NeverRun,

    /// <summary>Accepted and waiting for the one rebuild slot, which another project is holding.</summary>
    Queued,

    Running,
    Succeeded,
    Failed
}

/// <summary>
///     What the status endpoint answers with, polled by the web UI and by whatever drives the cron
///     (ADR-0004: no SignalR, no SSE). It lives in memory, so a replica that scaled to zero comes back
///     reporting <see cref="RefreshState.NeverRun" />; when the last refresh finished is durable and
///     is read from the index itself, on the project page beside this.
/// </summary>
public sealed record RefreshStatus(
    string Project,
    RefreshState State,
    string Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IndexSummary? Summary,
    string? Error);

/// <summary>
///     Whether a refresh was taken on. A refusal carries the prose saying why and what to do instead,
///     and <paramref name="OutOfDisk" /> tells the two refusals apart: waiting for the rebuild slot is
///     something a caller retries, and a full disk is something an operator has to act on.
/// </summary>
public sealed record RefreshRequest(bool Accepted, RefreshStatus Status, string? Refusal, bool OutOfDisk = false);

/// <summary>
///     Drives refreshes: one at a time across the whole server, refused when the disk would not hold
///     the shadow index, and reported through a status a caller polls. The work itself is
///     <see cref="IndexBuilder.RefreshAsync" />; this owns when it may start and what an operator can
///     see of it.
/// </summary>
public sealed class RefreshService(
    ControlDatabase control,
    IndexBuilder builder,
    ProjectIndexes indexes,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<RefreshService> logger)
{
    /// <summary>
    ///     Default for <c>Refresh:MinimumFreeBytes</c>. The shadow index is a second copy of the whole
    ///     project, so a refresh needs room for one more of what the project already occupies, and
    ///     <c>create_fts_index</c> needs working space on top; twice the live size is the judgement.
    ///     A project with no index yet has nothing to scale from, so the floor stands in — 512 MiB out
    ///     of the 8 GiB ceiling ADR-0003 measured, which is a first build of a large repository and
    ///     still leaves room for the other projects.
    /// </summary>
    private const long DefaultMinimumFreeBytes = 512L * 1024 * 1024;

    private readonly long _minimumFreeBytes =
        configuration.GetValue("Refresh:MinimumFreeBytes", DefaultMinimumFreeBytes);

    private readonly ConcurrentDictionary<string, RefreshStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>
    ///     Completes when every refresh accepted so far has finished. The chain is also the queue: one
    ///     rebuild runs at a time across the whole server (ADR-0003), and continuing the previous task
    ///     is what enforces that. Background work is handed off explicitly rather than dropped, so a
    ///     shutdown or a test can await it.
    /// </summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>What a caller polling the status endpoint gets, for a project that has never refreshed too.</summary>
    public RefreshStatus Status(string slug) =>
        _statuses.GetValueOrDefault(slug)
        ?? new RefreshStatus(slug, RefreshState.NeverRun, "No refresh has run on this replica", null, null, null, null);

    /// <summary>
    ///     Takes a refresh on, or refuses it. Returns as soon as the work is queued rather than when it
    ///     finishes: a refresh of a large project outlives any sensible HTTP timeout, so the caller —
    ///     the web UI or a Container Apps Job on a cron — polls <see cref="Status" /> for the rest.
    /// </summary>
    public RefreshRequest Request(Project project)
    {
        lock (_sync)
        {
            var current = Status(project.Slug);
            if (current.State is RefreshState.Queued or RefreshState.Running)
                return new RefreshRequest(false, current,
                    $"A refresh of project '{project.Slug}' is already {(current.State == RefreshState.Queued ? "queued" : "running")}. "
                    + $"Poll GET /api/projects/{project.Slug}/refresh for its progress instead of starting a second one.");

            if (InsufficientDisk(project.Slug) is { } refusal)
                return new RefreshRequest(false, current, refusal, true);

            var queued = new RefreshStatus(project.Slug, RefreshState.Queued, "Waiting for the rebuild slot",
                DateTimeOffset.UtcNow, null, null, null);
            _statuses[project.Slug] = queued;
            // Not fire-and-forget: this is Pending, RunAsync records every failure in the status rather
            // than letting one escape, and the continuation is what serialises the rebuilds.
            Pending = Pending.ContinueWith(_ => RunAsync(project), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return new RefreshRequest(true, queued, null);
        }
    }

    private async Task RunAsync(Project project)
    {
        // The refresh outlives the request that asked for it, so it takes the application's token and
        // not the caller's: a browser closing a tab must not abandon a rebuild half-way.
        var cancellationToken = lifetime.ApplicationStopping;
        var started = _statuses[project.Slug].StartedAt ?? DateTimeOffset.UtcNow;

        void Report(string phase) =>
            _statuses[project.Slug] = Status(project.Slug) with { State = RefreshState.Running, Phase = phase };

        void Fail(string error) =>
            _statuses[project.Slug] = new RefreshStatus(project.Slug, RefreshState.Failed, "Failed", started,
                DateTimeOffset.UtcNow, null, error);

        try
        {
            // Re-read: a queued refresh can have been waiting while the operator deleted the project,
            // and rebuilding one that no longer exists would put its index file back.
            if (await control.FindAsync(project.Slug, cancellationToken) is null)
            {
                Fail($"Project '{project.Slug}' was deleted before its refresh could start.");
                return;
            }

            // Checked again here and not only when it was accepted: another project's refresh may have
            // filled the disk in between, and that is exactly the condition ADR-0003 says to avoid.
            if (InsufficientDisk(project.Slug) is { } refusal)
            {
                Fail(refusal);
                return;
            }

            Report("Starting");
            var summary = await builder.RefreshAsync(project, Report, cancellationToken);
            _statuses[project.Slug] = new RefreshStatus(project.Slug, RefreshState.Succeeded, "Done", started,
                DateTimeOffset.UtcNow, summary, null);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Refresh of project {Project} finished", project.Slug);
        }
        catch (Exception ex)
        {
            // Safe to swallow, and the only safe thing to do: this is the top of a background task, so
            // an escaping exception would be an unobserved one and the operator would see a refresh
            // that never ends. The message is what the status endpoint reports instead.
            logger.LogError(ex, "Refresh of project {Project} failed", project.Slug);
            Fail(ex.Message);
        }
    }

    /// <summary>
    ///     The prose refusing a refresh that would not fit, or null when it fits. One method, because
    ///     the check runs twice — once to answer the caller, once when the queued work actually starts.
    /// </summary>
    private string? InsufficientDisk(string slug)
    {
        long free = indexes.FreeBytes();
        long required = Math.Max(_minimumFreeBytes, indexes.LiveSizeBytes(slug) * 2);
        return free >= required
            ? null
            : $"A refresh of project '{slug}' needs about {Mib(required)} free where the indexes live, and only {Mib(free)} is left. "
              + "That disk holds every project's index, the shadow index a refresh builds beside it, and the local copies, and it cannot be enlarged (ADR-0003). "
              + "Delete a project that is no longer needed, then refresh again.";
    }

    private static string Mib(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MiB");
}
