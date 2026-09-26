using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using CodeExplorer.Git;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using ModelContextProtocol;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Refresh;

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
///     is read from the index itself, on the project page beside this. What its <c>Error</c> may say is
///     <see cref="ExplainedFailureException" />'s rule.
/// </summary>
public sealed record RefreshStatus(
    string Project,
    RefreshState State,
    string Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IndexSummary? Summary,
    string? Error,
    RefreshProgress? Progress = null)
{
    /// <summary>
    ///     What each phase of this refresh cost, oldest first, filled as the refresh passes out of one
    ///     phase and into the next. <see cref="Phase" /> says what is happening now and is gone the
    ///     moment it changes; this is what a reader has afterwards, and is the difference between a
    ///     refresh whose cost can be read off its status and one that has to be measured again (#92).
    ///     A property with a default rather than a seventh positional parameter: only the refresh
    ///     itself ever has a timeline to put here, so an empty list is the honest answer at every other
    ///     call site and none of them has to say so.
    /// </summary>
    public IReadOnlyList<PhaseCost> Phases { get; init; } = [];
}

/// <summary>
///     Why a refresh was not taken on: the prose saying what to do instead, and the status code that
///     says which kind of answer it is. The code travels with the reason rather than being decoded
///     from a flag at the endpoint, so a third reason needs no third flag.
/// </summary>
/// <param name="Message">Operator-facing prose, returned as the API's <c>{ error }</c>.</param>
/// <param name="StatusCode">409 when the caller should simply try later, 507 when the disk is the problem.</param>
public sealed record RefreshRefusal(string Message, int StatusCode);

/// <summary>Where a refresh request got to. <paramref name="Refused" /> null means it was taken on.</summary>
public sealed record RefreshRequest(RefreshStatus Status, RefreshRefusal? Refused = null);

/// <summary>
///     Drives refreshes: one at a time across the whole server, refused when the disk would not hold
///     the shadow index, and reported through a status a caller polls. The work itself is
///     <see cref="ProjectRefresh" />; this owns when it may start and what an operator can see of it.
/// </summary>
public sealed class RefreshService(
    ProjectRefresh refresh,
    ProjectIndexes indexes,
    GitClones clones,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<RefreshService> logger)
{
    /// <summary>
    ///     Default for <c>Refresh:MinimumFreeBytes</c>, the least free space a refresh is granted; what a
    ///     shadow index costs beyond it is <see cref="ProjectIndexes.RoomForShadow" />'s judgement. A
    ///     project with no index yet has nothing to scale from, so the floor stands in — 512 MiB out of
    ///     the 8 GiB ceiling ADR-0003 measured, which is a first build of a large repository and still
    ///     leaves room for the other projects.
    /// </summary>
    private const long _defaultMinimumFreeBytes = 512L * 1024 * 1024;

    /// <summary>
    ///     What a fetch into an existing clone is assumed to add, as a fraction of what that clone
    ///     already occupies. A fetch transfers a delta and then repacks, and a repack can hold the old
    ///     and the new pack at once — a quarter is the judgement, and it is a guess rather than a
    ///     measurement because the real figure depends on how much was committed since the last
    ///     refresh. A project whose clones are missing entirely gets the floor instead, which is the
    ///     one case where this system genuinely cannot know the size before downloading it (ADR-0007).
    /// </summary>
    private const int _cloneGrowthDivisor = 4;

    private readonly long _minimumFreeBytes =
        configuration.GetValue("Refresh:MinimumFreeBytes", _defaultMinimumFreeBytes);

    private readonly ConcurrentDictionary<string, RefreshStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>
    ///     Completes when every refresh accepted so far has finished. The chain is also the queue: one
    ///     rebuild runs at a time across the whole server (ADR-0003), and continuing the previous task
    ///     is what enforces that. Background work is handed off explicitly rather than dropped, so a
    ///     shutdown or a test can await it.
    /// </summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>
    ///     The status of a refresh this replica knows about, or null when it has run none for the
    ///     project. Null does not mean the project is unknown — only the control database can say that.
    /// </summary>
    public RefreshStatus? Find(string slug) => _statuses.GetValueOrDefault(slug);

    /// <summary>What a caller polling the status endpoint gets, for a project that has never refreshed too.</summary>
    public RefreshStatus Status(string slug) =>
        Find(slug)
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
                return new RefreshRequest(current, new RefreshRefusal(
                    $"A refresh of project '{project.Slug}' is already {(current.State == RefreshState.Queued ? "queued" : "running")}. "
                    + $"Poll GET /api/projects/{project.Slug}/refresh for its progress instead of starting a second one.",
                    StatusCodes.Status409Conflict));

            if (InsufficientDisk(project.Slug) is { } refusal) return new RefreshRequest(current, refusal);

            var queued = new RefreshStatus(project.Slug, RefreshState.Queued, "Waiting for the rebuild slot",
                DateTimeOffset.UtcNow, null, null, null);
            _statuses[project.Slug] = queued;
            // Not fire-and-forget: this is Pending, RunAsync records every failure in the status rather
            // than letting one escape, and the continuation is what serialises the rebuilds.
            Pending = Pending.ContinueWith(_ => RunAsync(project), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return new RefreshRequest(queued);
        }
    }

    private async Task RunAsync(Project project)
    {
        // The refresh outlives the request that asked for it, so it takes the application's token and
        // not the caller's: a browser closing a tab must not abandon a rebuild half-way.
        var cancellationToken = lifetime.ApplicationStopping;
        // Set when the refresh was queued, which is what an operator watching a queue wants to see.
        var started = _statuses[project.Slug].StartedAt;

        // What this refresh has spent so far, kept beside the status because the status itself holds
        // only the phase running now (#92).
        var timeline = new PhaseTimeline();

        // The phase text stays the status's own field as well as the progress's, so a caller that only
        // reads `phase` — the cron, an older client — keeps working unchanged.
        void Report(RefreshProgress progress)
        {
            var current = Status(project.Slug);
            _statuses[project.Slug] = current with
            {
                State = RefreshState.Running,
                Phase = progress.Phase,
                Progress = progress,
                // Null where the report did not turn the page to a new phase, which is most of them: a
                // counting step reports every 200 items, and a snapshot per report would be a list
                // rebuilt thousands of times to say exactly what it said before.
                Phases = timeline.Record(progress) ?? current.Phases
            };
        }

        void Fail(string error) =>
            _statuses[project.Slug] = new RefreshStatus(project.Slug, RefreshState.Failed, "Failed", started,
                DateTimeOffset.UtcNow, null, error) { Phases = timeline.Close() };

        // Whether this refresh restored the index without its full-text index, which only its own swap
        // makes good: a refresh that ends any other way puts one back that has it.
        bool restoredWithoutFullText = false;
        try
        {
            // Reported before the restore rather than after the check, so a restore that takes minutes
            // shows as a refresh running and not as one still waiting for the slot.
            Report(new RefreshProgress(RefreshProgress.FetchStep, RefreshProgress.TotalStepCount, RefreshProgress.StartPhase));

            // Before the check below, which sizes the shadow from the live file: on a wiped disk there is
            // none until the durable copy is restored, and the check would size the shadow of a large
            // project at the floor (#229). It reports a phase of its own, so a restore that fails is
            // not reported as a refresh that failed while starting (#290).
            restoredWithoutFullText = await indexes.RestoreForRefreshAsync(project.Slug, Report, cancellationToken);

            // Checked again here and not only when it was accepted: another project's refresh may have
            // filled the disk in between, and that is exactly the condition ADR-0003 says to avoid.
            if (InsufficientDisk(project.Slug) is { } refusal)
            {
                Fail(refusal.Message);
                return;
            }

            var summary = await refresh.RunAsync(project, Report, cancellationToken);
            // Swapped out for the shadow, which built its own full-text index.
            restoredWithoutFullText = false;
            _statuses[project.Slug] = new RefreshStatus(project.Slug, RefreshState.Succeeded, "Done", started,
                DateTimeOffset.UtcNow, summary, null) { Phases = timeline.Close() };
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Refresh of project {Project} finished", project.Slug);
        }
        catch (Exception ex)
        {
            // Safe to swallow, and the only safe thing to do: this is the top of a background task, so
            // an escaping exception would be an unobserved one and the operator would see a refresh
            // that never ends. The status reports it instead, and the log keeps it whole.
            logger.LogError(ex, "Refresh of project {Project} failed", project.Slug);
            Fail(ex is McpException or ExplainedFailureException
                ? ex.Message
                : Unexplained(project.Slug, Status(project.Slug).Phase));
        }
        finally
        {
            // After the status says the refresh failed, so an operator is not left waiting on a repair
            // to learn that; Pending still covers it, so the next refresh queues behind it.
            if (restoredWithoutFullText) await RestoreWithFullTextAsync(project.Slug, cancellationToken);
        }
    }

    /// <summary>
    ///     Puts back a full-text index for a refresh that restored without one and then failed (#290).
    /// </summary>
    private async Task RestoreWithFullTextAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            await indexes.RestoreWithFullTextAsync(slug, cancellationToken);
        }
        catch (Exception ex)
        {
            // Safe to swallow: the refresh has already failed and said why, and the index it restored
            // still answers every search, by substring scan. The log line is how an operator learns the
            // project stays that way until a refresh succeeds.
            logger.LogWarning(ex, "Project {Project} could not be given back its full-text index after its "
                                  + "refresh failed; it is searched by substring scan until a refresh succeeds",
                slug);
        }
    }

    /// <summary>
    ///     What the status says of a failure whose message was not written for its reader: .NET's and
    ///     DuckDB's name the files they could not write, and whoever reads the status cannot reach the
    ///     server's disk (#262). The phase is the last one the refresh reported, so the sentence stays
    ///     true as the steps change; the log has the rest.
    /// </summary>
    private static string Unexplained(string slug, string phase) =>
        $"The refresh of project '{slug}' failed in the phase \"{phase}\". The operator log has the details.";

    /// <summary>
    ///     The refusal for a refresh that would not fit, or null when it fits. One method, because the
    ///     check runs twice — once to answer the caller, once when the queued work actually starts.
    /// </summary>
    private RefreshRefusal? InsufficientDisk(string slug)
    {
        var index = indexes.RoomForShadow(slug, _minimumFreeBytes);
        // The clones are part of what a refresh needs room for since ADR-0007 made them full: they are
        // fetched into before anything is built, so a check that sized only the shadow index would pass
        // and then fill the disk during the transfer, which is the failure this gate exists to prevent.
        var room = index with { Required = index.Required + clones.Footprint(slug) / _cloneGrowthDivisor };
        return room.Enough
            ? null
            : new RefreshRefusal(
                $"A refresh of project '{slug}' needs about {Mib(room.Required)} free where the indexes live, and only {Mib(room.Free)} is left. "
                + "That disk holds every project's index, the shadow index a refresh builds beside it, and the local copies — which hold full git history since ADR-0007 — and it cannot be enlarged (ADR-0003). "
                + "Delete a project that is no longer needed, then refresh again.",
                // 507 rather than another 409: a cron reading only the status line still learns that
                // this is about storage and not about another rebuild holding the slot.
                StatusCodes.Status507InsufficientStorage);
    }

    /// <summary>
    ///     MiB, not <c>ToolReply.Bytes</c>'s scaled MB: this figure sits next to the 8 GiB ceiling
    ///     ADR-0003 documents, and the two are only comparable in the same units.
    /// </summary>
    private static string Mib(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MiB");
}
