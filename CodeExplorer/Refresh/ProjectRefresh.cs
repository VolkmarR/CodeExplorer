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
        // Read before the project is: a delete counted after this is one the publish below refuses to
        // build over, and one counted before it has already taken the project out of the control
        // database, which the check that follows sees. A refresh that outlived a delete used to put the
        // deleted project's index and durable copy back (GHSA-253f-grfp-cqq7).
        long discards = indexes.DiscardCount(project.Slug);
        // Re-read and not taken from the caller: a queued refresh can have been waiting while the
        // operator deleted the project, or deleted it and created another under the slug, whose record
        // is the one to build.
        project = await control.FindAsync(project.Slug, cancellationToken)
                  ?? throw new ExplainedFailureException(Deleted(project, "before its refresh could start"));

        var repositories = await control.ListRepositoriesAsync(project.Slug, cancellationToken);
        var opened = new List<OpenedRepository>();
        var skipped = new List<string>();
        var condition = new PublishCondition(discards,
            async token => await control.FindAsync(project.Slug, token) is not null);
        bool kept = false;
        try
        {
            // Inside the try, so a fetch that ends the refresh — cancellation above all — still closes
            // the copies opened before it. Left open, their pack files stay held, and on Windows a later
            // removal of the repository fails on them (#241).
            await FetchAsync(repositories, opened, skipped, report, cancellationToken);

            // Every repository failed, so the shadow would be an empty index and the swap would throw
            // the project's whole searchable history away over what is usually a transient network
            // fault. Leaving the old index serving, and saying so, is the answer an operator can act on.
            // The skipped repositories' messages are McpExceptions' own, already scrubbed of server
            // paths (#232), so the whole sentence is one the status may report as written.
            if (repositories.Count > 0 && opened.Count == 0)
                throw new ExplainedFailureException(
                    $"No repository of project '{project.Slug}' could be read, so its index was left as it was: "
                    + string.Join(" ", skipped));

            IndexSummary summary;
            try
            {
                report(new RefreshProgress(RefreshProgress.IngestStep, _totalSteps, RefreshProgress.IngestPhase));
                bool published;
                using (var shadow = await indexes.CreateShadowAsync(project.Slug, cancellationToken))
                {
                    summary = await builder.FillAsync(shadow, opened, repositories, project.SingleRepository,
                        report, cancellationToken);
                    // Reported before the publish waits on the writer gate, so a wait behind a restore of
                    // the same project is billed to the store it is holding up.
                    report(new RefreshProgress(RefreshProgress.StoreStep, _totalSteps, RefreshProgress.StorePhase));
                    // No phase names the flush to disk: it is the CHECKPOINT the publish runs on the
                    // shadow after storing it and before the swap (#242). It lands after StoreStep is
                    // reported, outside the step-3 window #91 is about, so its half-second is billed to
                    // the store rather than to nothing.
                    published = await indexes.PublishShadowAsync(shadow, condition, report, cancellationToken);
                }

                // Thrown inside the try, so the catch below removes the shadow the publish refused.
                if (!published)
                    throw new ExplainedFailureException(Deleted(project,
                        "while its refresh was running, so nothing the refresh built was kept"));
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
            kept = true;
            return summary with { Skipped = skipped };
        }
        finally
        {
            foreach (var open in opened) open.LocalCopy.Dispose();
            // After the copies are closed, which on Windows is what lets them be removed.
            if (!kept) await RemoveClonesIfDeletedAsync(project.Slug, condition);
        }
    }

    /// <summary>
    ///     Removes the local copies of a project deleted under a refresh that then failed, however it
    ///     failed, once the refresh has closed them. The delete removed them too, but a fetch that ran
    ///     after it cloned them again, and a copy is fetched from its own <c>origin</c>: left behind, a
    ///     project created later under the slug, with a repository of the same slug, would index the
    ///     deleted project's remote (GHSA-253f-grfp-cqq7). No refresh of a new project under the slug can
    ///     be running meanwhile, because the service takes one refresh per slug at a time.
    /// </summary>
    private async Task RemoveClonesIfDeletedAsync(string slug, PublishCondition condition)
    {
        try
        {
            // Not the refresh's token: this is cleanup after the refresh ended, cancelled or not.
            if (!await indexes.StillHoldsAsync(slug, condition, CancellationToken.None))
                await clones.RemoveAsync(slug, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Safe to swallow: the refresh is already failing with its own reason, and an escaping
            // exception here would replace it. The log line is how a copy left behind is noticed.
            logger.LogWarning(ex, "The local copies of deleted project {Project} could not be removed", slug);
        }
    }

    /// <summary>
    ///     Why a refresh of a deleted project ended without an index, for the status an operator reads.
    ///     InvalidOperationException carries it, for the reason the no-repository failure above says.
    /// </summary>
    private static string Deleted(Project project, string when) => $"Project '{project.Slug}' was deleted {when}.";

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
                // Empty, LFS and a local repository switched off are decided behind the open (CloneOpen); a
                // refusal holds nothing to dispose.
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
