namespace CodeExplorer;

/// <summary>
///     What a warm-up did to one project. <paramref name="Ready" /> false with no
///     <paramref name="Error" /> is a project that has never been indexed and has no durable copy to
///     restore — an answer, not a failure: the operator refreshes it.
/// </summary>
public sealed record WarmedProject(string Project, bool Ready, string? Error = null);

/// <summary>
///     The warm-up (CONTEXT.md): attaching every project ahead of the working day so that the first
///     agent of the morning does not wait for a restore. It is an operator action an external cron
///     calls, and deliberately not an in-process timer — a replica that has scaled to zero has nothing
///     running to fire one (ADR-0004).
///     It lives beside the refresh rather than in <c>Operator/</c> because ADR-0005 puts the refresh
///     and warm-up endpoints together: both are work a cron drives against a project's index, and both
///     report to a caller that never sees a UI.
/// </summary>
public sealed class WarmUp(ControlDatabase control, ProjectIndexes indexes, ILogger<WarmUp> logger)
{
    /// <summary>
    ///     Opens every project in turn, which restores each from its durable copy if the disk no longer
    ///     has it. One at a time, because the restores share one disk and one buffer pool and racing
    ///     them would only make the slowest of them slower.
    ///     A project that throws is reported and the walk continues: a cron calling this before working
    ///     hours wants the other projects warm regardless, and one unreachable copy is not a reason to
    ///     leave them cold.
    /// </summary>
    public async Task<IReadOnlyList<WarmedProject>> RunAsync(CancellationToken cancellationToken)
    {
        var warmed = new List<WarmedProject>();
        foreach (var project in await control.ListAsync(cancellationToken))
            try
            {
                using var lease = await indexes.OpenAsync(project.Slug, cancellationToken);
                warmed.Add(new WarmedProject(project.Slug, lease is not null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Safe to swallow: the reason is reported in this project's row, the remaining projects
                // are still warmed, and a cancellation is the caller giving up rather than a project
                // failing — which is why it is the one exception that still ends the walk.
                logger.LogError(ex, "Warming project {Project} failed", project.Slug);
                // The log carries the exception and the response does not. A warm-up walks the whole
                // control database, and a message from anywhere down that path is the one place a
                // stored credential could reach a caller (CODING_STANDARDS, Git).
                warmed.Add(new WarmedProject(project.Slug, false,
                    $"Project '{project.Slug}' could not be warmed; the server log says why."));
            }

        return warmed;
    }
}
