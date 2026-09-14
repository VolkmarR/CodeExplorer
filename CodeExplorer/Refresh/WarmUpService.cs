using System.Globalization;

namespace CodeExplorer;

/// <summary>
///     Runs the warm-up in the background, on start and optionally on a repeat, for a deployment that
///     keeps a replica running. Off unless <c>Refresh:WarmUpOnStart</c> is set, because what it can and
///     cannot do depends entirely on how the app is hosted:
///     it cannot replace the external cron ADR-0004 chose. A hosted service only runs while the
///     container does, and under scale to zero the call that has to happen before working hours is also
///     the call that wakes the container — which no in-process timer can do for itself. Worse, wakes are
///     frequent there, and warming every project on each one is the cost lazy attach exists to avoid:
///     an off-hours wake would pay for everyone's restore rather than one project's (#9).
///     With <c>minReplicas</c> at one it is straightforwardly useful, and that is the deployment it is
///     for. The endpoint stays either way, and is still what a cron calls.
/// </summary>
public sealed class WarmUpService(WarmUp warmUp, IConfiguration configuration, ILogger<WarmUpService> logger)
    : BackgroundService
{
    /// <summary>
    ///     Default for <c>Refresh:WarmUpDelaySeconds</c>. A container usually starts because a request
    ///     arrived, and that request is restoring the one project it asked for; starting every other
    ///     project's restore alongside it would make the caller that paid for the wake wait longest.
    ///     Thirty seconds is long enough for an ordinary first request to be answered and short enough
    ///     that a replica started deliberately is warm before anyone notices.
    /// </summary>
    private const int DefaultDelaySeconds = 30;

    private readonly bool _enabled = configuration.GetValue("Refresh:WarmUpOnStart", false);

    private readonly TimeSpan _delay =
        TimeSpan.FromSeconds(configuration.GetValue("Refresh:WarmUpDelaySeconds", DefaultDelaySeconds));

    /// <summary>
    ///     <c>Refresh:WarmUpIntervalMinutes</c>: how long after one warm-up the next runs. Absent means
    ///     once per start, which is the useful shape for a replica that is restarted rather than kept.
    ///     A repeat matters only where something else can remove an index file under a running replica.
    /// </summary>
    private readonly TimeSpan _interval =
        TimeSpan.FromMinutes(configuration.GetValue("Refresh:WarmUpIntervalMinutes", 0));

    /// <summary>
    ///     Completes when the warm-up this service was going to do is done, so a test can await it
    ///     rather than poll. Background work is handed off explicitly here as everywhere else
    ///     (CODING_STANDARDS, Async); <see cref="BackgroundService.ExecuteTask" /> is protected, and a
    ///     repeating service never completes, so it is the first pass that is reported.
    /// </summary>
    public Task Warmed => _warmed.Task;

    private readonly TaskCompletionSource _warmed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _warmed.TrySetResult();
            return;
        }

        // Yield first: a BackgroundService that runs synchronously to its first await blocks the host
        // from finishing startup, and this one is deliberately the last thing that should hold it up.
        await Task.Yield();
        try
        {
            await Task.Delay(_delay, stoppingToken);
            await WarmAsync(stoppingToken);
            _warmed.TrySetResult();

            if (_interval <= TimeSpan.Zero) return;

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await WarmAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Safe to swallow: the host is stopping, which is the ordinary end of this service and not
            // a failure to report. Nothing is half-done — a warm-up only reads and restores, and a
            // restore that was interrupted leaves no file behind for the next start to find.
            _warmed.TrySetResult();
        }
    }

    /// <summary>
    ///     One pass, with every failure kept inside it. An exception escaping <see cref="ExecuteAsync" />
    ///     stops the whole host by default, and a warm-up is an optimisation: a control database that
    ///     could not be read is a reason to serve cold, never a reason to take the server down.
    /// </summary>
    private async Task WarmAsync(CancellationToken stoppingToken)
    {
        try
        {
            var warmed = await warmUp.RunAsync(stoppingToken);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Warmed {Ready} of {Total} projects on start",
                    warmed.Count(project => project.Ready).ToString(CultureInfo.InvariantCulture),
                    warmed.Count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Safe to swallow, for the reason above: every project the walk reached is warm, the rest
            // are restored on first connection as they would have been without this service at all.
            logger.LogError(ex, "The warm-up on start did not finish");
        }
    }
}
