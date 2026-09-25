using CodeExplorer.Control;
using CodeExplorer.Git;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using ModelContextProtocol;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Refresh;

/// <summary>
///     One refresh of one project (CONTEXT.md): bring every local copy up to date, read them into a
///     shadow index beside the live one, and swap it in. The live index answers every query
///     throughout, so a project is never searchable in a half-built state. What it may not do —
///     one at a time, and not without the disk for it — is <see cref="RefreshService" />'s to decide.
/// </summary>
public sealed class ProjectRefresh(
    ControlDatabase control,
    GitClones clones,
    IndexBuilder builder,
    DurableIndex durable,
    ProjectIndexes indexes,
    ILogger<ProjectRefresh> logger)
{
    private const int _totalSteps = RefreshProgress.TotalStepCount;

    /// <param name="project">The project to refresh, as the control database holds it.</param>
    /// <param name="report">
    ///     Called with the phase reached, for the status a web UI and an external cron poll. Synchronous,
    ///     so a status read straight after a phase change sees it.
    /// </param>
    /// <param name="cancellationToken">Threaded through the fetch, the ingest and the swap.</param>
    public async Task<IndexSummary> RunAsync(Project project, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        var repositories = await control.ListRepositoriesAsync(project.Slug, cancellationToken);
        var opened = new List<OpenedRepository>();
        var skipped = new List<string>();
        try
        {
            // Inside the try, so a fetch that ends the refresh — cancellation above all — still closes
            // the copies opened before it. Left open, their pack files stay held, and on Windows a later
            // removal of the repository fails on them (#241).
            await FetchAsync(repositories, opened, skipped, report, cancellationToken);

            // Every repository failed, so the shadow would be an empty index and the swap would throw
            // the project's whole searchable history away over what is usually a transient network
            // fault. Leaving the old index serving, and saying so, is the answer an operator can act on.
            // InvalidOperationException and not McpException: no MCP tool is on this path, and the
            // refresh reports a failure through its status, which is where this message ends up.
            if (repositories.Count > 0 && opened.Count == 0)
                throw new InvalidOperationException(
                    $"No repository of project '{project.Slug}' could be read, so its index was left as it was: "
                    + string.Join(" ", skipped));

            IndexSummary summary;
            try
            {
                report(new RefreshProgress(RefreshProgress.IngestStep, _totalSteps, RefreshProgress.IngestPhase));
                // Scoped so the shadow connection is closed before the swap: the file cannot be moved
                // over the live one while the instance still holds it open.
                using (var shadow = await indexes.CreateShadowAsync(project.Slug, cancellationToken))
                {
                    summary = await builder.FillAsync(shadow, opened, repositories, project.SingleRepository,
                        report, cancellationToken);
                    report(new RefreshProgress(RefreshProgress.StoreStep, _totalSteps, RefreshProgress.StorePhase));
                    // Exported from the shadow rather than from the live index after the swap, which is
                    // what the tables about to be swapped in are. Doing it here means the export needs
                    // no second attach of the live catalog — one that would quietly re-bind a connection
                    // ADR-0003 says the swap must strand — and a store that is unreachable discards the
                    // shadow and leaves the old index serving, like any other failure of a build.
                    await durable.StoreAsync(shadow.Connection, project.Slug, cancellationToken);
                }
                // No phase names the flush to disk because there is no separable call to name: the
                // server issues no explicit CHECKPOINT, so the shadow flushes when the using above
                // disposes it and again on the DETACH inside the swap. Both land after StoreStep is
                // reported, outside the step-3 window #91 is about, so its half-second is billed to
                // the store rather than to nothing.

                report(new RefreshProgress(RefreshProgress.SwapStep, _totalSteps, RefreshProgress.SwapPhase));
                await indexes.SwapShadowAsync(project.Slug, cancellationToken);
            }
            catch
            {
                // The live index is untouched and still serving; only the half-written shadow goes.
                await indexes.DiscardShadowAsync(project.Slug, CancellationToken.None);
                throw;
            }

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "Refreshed project {Project}: {Repositories} repositories, {Files} files, {Lines} lines",
                    project.Slug, summary.Repositories, summary.Files, summary.Lines);
            return summary with { Skipped = skipped };
        }
        finally
        {
            foreach (var open in opened) open.LocalCopy.Dispose();
        }
    }

    /// <summary>
    ///     Brings every local copy up to date and opens it, before the index is touched: a fetch that
    ///     fails leaves the previous index serving. A repository that cannot be read, or that must not
    ///     be, is named in <paramref name="skipped" /> instead of failing the project. The copies go into
    ///     <paramref name="opened" />, which the caller owns and closes however this ends.
    /// </summary>
    private async Task FetchAsync(IReadOnlyList<ProjectRepository> repositories, List<OpenedRepository> opened,
        List<string> skipped, Action<RefreshProgress> report, CancellationToken cancellationToken)
    {
        int fetched = 0;
        foreach (var repository in repositories)
        {
            // Reported before the fetch, and counted as done after: an operator watching wants to know
            // which repository is being transferred now, not which one finished last.
            report(new RefreshProgress(RefreshProgress.FetchStep, _totalSteps, $"Fetching '{repository.Slug}'", fetched++,
                repositories.Count));
            try
            {
                // Empty and LFS are decided behind the open (CloneOpen); a refusal holds nothing to dispose.
                var open = await clones.OpenRefreshedAsync(repository, cancellationToken);
                if (open is CloneOpen.Refused refused) skipped.Add(refused.Explanation);
                else opened.Add(new OpenedRepository(repository, ((CloneOpen.Opened)open).Copy));
            }
            catch (McpException ex)
            {
                // Safe to swallow: the reason is reported in the summary in place of the repository,
                // and the other repositories still get indexed. A local copy libgit2 cannot open
                // arrives here too, already an McpException naming the repository (#232).
                skipped.Add(ex.Message);
            }
        }
    }
}
