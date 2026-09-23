using System.Globalization;

namespace CodeExplorer.Refresh;

/// <summary>
///     Runs the warm-up once, when the application starts, for a deployment that keeps a replica
///     running. Off unless <c>Refresh:WarmUpOnStart</c> is set, because what it can and cannot do
///     depends entirely on how the app is hosted:
///     it cannot replace the external cron ADR-0004 chose. A hosted service only runs while the
///     container does, and under scale to zero the call that has to happen before working hours is also
///     the call that wakes the container — which nothing inside a stopped container can make. Worse,
///     wakes are frequent there, and warming every project on each one is the cost lazy attach exists
///     to avoid: an off-hours wake would pay for everyone's restore rather than one project's (#9).
///     With <c>minReplicas</c> at one it is straightforwardly useful, and that is the deployment it is
///     for. The endpoint stays either way, and is still what a cron calls.
///     Once, at the start, and deliberately not on a repeat: a start is the only moment a replica's
///     disk is empty, and nothing removes an index file underneath a running one. Deleting a project
///     is the exception and is meant to leave it cold; a refresh replaces the file rather than
///     removing it.
/// </summary>
public sealed class WarmUpService(WarmUp warmUp, IConfiguration configuration, ILogger<WarmUpService> logger)
    : BackgroundService
{
    private readonly bool _enabled = configuration.GetValue("Refresh:WarmUpOnStart", false);

    private readonly TaskCompletionSource _warmed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Completes when the warm-up is done, and immediately when there was none to do, so a test can
    ///     await the work rather than poll for it. Background work is handed off explicitly here as
    ///     everywhere else (CODING_STANDARDS, Async); <see cref="BackgroundService.ExecuteTask" /> is
    ///     protected, which is why this is reported separately.
    /// </summary>
    public Task Warmed => _warmed.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _warmed.TrySetResult();
            return;
        }

        // Yield first, and this is the whole of the wait: a BackgroundService that runs synchronously
        // to its first await blocks the host from finishing startup, so the yield is what lets the
        // server start listening while the projects restore behind it. Warming begins at once from
        // there — a replica started deliberately should be warm as soon as it can be, and holding the
        // work back by a fixed delay would only be guessing at what else the first seconds are for.
        await Task.Yield();
        try
        {
            var warmed = await warmUp.RunAsync(stoppingToken);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Warmed {Ready} of {Total} projects on start",
                    warmed.Count(project => project.Ready).ToString(CultureInfo.InvariantCulture),
                    warmed.Count.ToString(CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException)
        {
            // Safe to swallow: the host is stopping, which is the ordinary end of this service and not
            // a failure to report. Nothing is half-done — a warm-up only reads and restores, and a
            // restore that was interrupted leaves no file behind for the next start to find.
        }
        catch (Exception ex)
        {
            // Safe to swallow, and the only safe thing to do: an exception escaping ExecuteAsync stops
            // the whole host by default, and a warm-up is an optimisation. A control database that
            // could not be read is a reason to serve cold — every project is restored on first
            // connection as it would have been without this service at all — never a reason to take
            // the server down.
            logger.LogError(ex, "The warm-up on start did not finish");
        }
        finally
        {
            _warmed.TrySetResult();
        }
    }
}
