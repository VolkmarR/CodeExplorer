using CodeExplorer.Control;
using CodeExplorer.Git;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;

namespace CodeExplorer.Operator;

/// <summary>
///     What a project's index holds. <paramref name="BuiltAt" /> is null when the project has never
///     been indexed — a state every project passes through, not a failure to report.
/// </summary>
public sealed record ProjectIndexStatus(DateTimeOffset? BuiltAt, bool FtsIndexed, int Files, long Lines)
{
    public static ProjectIndexStatus None { get; } = new(null, false, 0, 0);
}

/// <summary>A project as the project list shows it.</summary>
public sealed record ProjectSummary(
    string Slug,
    string Name,
    bool SingleRepository,
    int Repositories,
    ProjectIndexStatus Index);

/// <summary>
///     One repository as the project page shows it: what the operator configured, plus where the last
///     build found it. The git fields are null for a repository added since the last build, which is
///     how the page shows that a rebuild is owed. The credential appears only as set or not set.
/// </summary>
public sealed record RepositoryDetail(
    string Slug,
    string Url,
    bool HasCredential,
    string? HeadCommit,
    int? FileCount,
    long? LineCount,
    long Commits = 0,
    RepositoryCommit? NewestCommit = null);

/// <summary>
///     The newest commit imported for a repository, as the project page names it. Null where no history
///     was imported — which the page says in those words, because an operator seeing nothing would
///     otherwise read it as a repository nobody has touched.
/// </summary>
public sealed record RepositoryCommit(string Sha, string AuthorName, DateTimeOffset AuthoredAt, string Subject);

/// <summary>
///     What the overview page draws: the index's overview computed live over the page's filters and
///     without the project's excluded paths (#216). It is not the row <c>project_overview</c> answers
///     from, and may show different numbers than an agent is told once a filter or an exclusion
///     applies; with neither, the sections are computed by the same statements and agree.
///     Either <see cref="Overview" /> or <see cref="Unavailable" /> is set. <see cref="Unavailable" /> is
///     the reader's own prose saying why there is nothing to show — a project never built, one whose
///     first build is still running, a repository filter naming none of its repositories — and it is
///     carried rather than collapsed to null so that the page says what an agent asking the same
///     question is told, instead of showing an operator a blank where there is an explanation.
///     It is its own response rather than a field on <see cref="ProjectDetail" /> because it is the
///     page's heaviest read and the only one that grows with the project, so the header and the
///     repository table are not held behind it.
/// </summary>
/// <param name="Overview">The sections, over the page's scope.</param>
/// <param name="Unavailable">Why there are none.</param>
/// <param name="ExcludedPatterns">How many patterns the project's setting holds, applied or not.</param>
/// <param name="Excluded">
///     How many files the patterns kept out of each kind of section; null where nothing was kept out
///     because the setting is empty or the page asked to see the excluded paths.
/// </param>
/// <param name="Cards">
///     The cards only the page draws — Hotspots (#211), Most authors per file (#212), Folders that change together (#213),
///     Files added and deleted (#214) — set with
///     <see cref="Overview" />. Beside the overview rather than in it, because <see cref="IndexOverview" />
///     is also the stored row and the <c>project_overview</c> reply, and neither has them.
/// </param>
public sealed record ProjectOverviewDetail(
    IndexOverview? Overview,
    string? Unavailable,
    int ExcludedPatterns = 0,
    OverviewExcluded? Excluded = null,
    OverviewCards? Cards = null);

/// <summary>
///     The overview page's filters, read off its URL. <see cref="Days" /> is clamped the way every
///     window is (<see cref="HistoryWindow.Ending" />); a blank <see cref="Repository" /> is the whole
///     project; <see cref="ShowExcluded" /> lifts the project's excluded paths for this view only.
/// </summary>
public sealed record OverviewFilter(int Days, string? Repository, bool ShowExcluded);

/// <summary>
///     What the settings page's Suggest button reads (#217): the proposals, or, where the project has
///     no index to propose from, the reader's sentence saying why and no proposals.
/// </summary>
public sealed record ExcludedPathSuggestionsDetail(IReadOnlyList<ExcludedPathSuggestion> Suggestions, string? Unavailable);

/// <summary>A project as its own page shows it.</summary>
public sealed record ProjectDetail(
    string Slug,
    string Name,
    bool SingleRepository,
    ProjectIndexStatus Index,
    IReadOnlyList<RepositoryDetail> Repositories);

/// <summary>
///     The operator UI's reads and its destructive actions, both of which span modules: what exists
///     comes from the control database and how much of it is searchable comes from the index, and
///     removing a project touches the control database, the index file and the local copies. Creating
///     a project and adding a repository touch only the control database and stay in <c>Control/</c>;
///     putting the composition here keeps <c>Control/</c> from depending on <c>Index/</c>, which
///     already depends on it (ADR-0005).
/// </summary>
public sealed class ProjectOverview(
    ControlDatabase control,
    IndexReaders readers,
    GitClones clones,
    IConfiguration configuration,
    ILogger<ProjectOverview> logger)
{
    /// <summary>The ceiling the folder coupling card pairs under, the co-change tool's own (#213).</summary>
    private readonly int _maxCommitPaths = CoChangeCeiling.From(configuration);

    /// <summary>
    ///     One index read per project. An operator administers projects by hand, so the list is the
    ///     length of a screen and a single query joining across attached databases would buy nothing.
    ///     The repository counts are the exception and are read in one query, because that one was a
    ///     control-database connection per row of the page rather than a read of an attached index
    ///     (#149) — and only the count is wanted here, never the rows.
    /// </summary>
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var projects = await control.ListAsync(cancellationToken);
        var repositories = await control.CountRepositoriesAsync(cancellationToken);
        var summaries = new List<ProjectSummary>(projects.Count);
        foreach (var project in projects)
        {
            // Peeked, not opened: the list touches every project, and restoring every durable copy to
            // draw one page is exactly what lazy attach exists to avoid (#9). A project whose file the
            // last shutdown wiped reads as not indexed here until someone opens it.
            var (status, _) = await ReadIndexAsync(project.Slug, false, cancellationToken);
            summaries.Add(new ProjectSummary(project.Slug, project.Name, project.SingleRepository,
                repositories.GetValueOrDefault(project.Slug), status));
        }

        return summaries;
    }

    /// <summary>One project's page. The project is the route's, so there is no "none" to answer here.</summary>
    public async Task<ProjectDetail> FindAsync(Project project, CancellationToken cancellationToken)
    {
        var configured = await control.ListRepositoriesAsync(project.Slug, cancellationToken);
        // One project's page restores that one project, which is the whole of what lazy attach asks:
        // a deliberate visit to a project pays for it, and the list above does not pay for all of them.
        var (status, indexedRepositories) = await ReadIndexAsync(project.Slug, true, cancellationToken);
        var indexed = indexedRepositories.ToDictionary(r => r.Slug);
        // Driven by the configured repositories, not the indexed ones: a repository removed since the
        // last build is gone from the page immediately, even though its files are still searchable.
        var repositories = configured
            .Select(r => indexed.GetValueOrDefault(r.Slug) is { } built
                ? new RepositoryDetail(r.Slug, r.Url, r.HasCredential, built.HeadCommit, built.FileCount,
                    built.LineCount, built.Commits,
                    built.NewestCommit is { } newest
                        ? new RepositoryCommit(newest.Sha, newest.AuthorName, newest.AuthoredAt, newest.Subject)
                        : null)
                : new RepositoryDetail(r.Slug, r.Url, r.HasCredential, null, null, null))
            .ToList();
        return new ProjectDetail(project.Slug, project.Name, project.SingleRepository, status, repositories);
    }

    /// <summary>
    ///     One project's overview as its page shows it: computed live from the index over the page's
    ///     filters, leaving out the paths the project's setting names (#216). Live rather than the
    ///     stored row so that a changed setting applies on the next load without a rebuild, and so that
    ///     the page can be filtered at all. A project with no index is not a failure here — every
    ///     project passes through that state — so the reader's refusal is carried through as prose
    ///     rather than raised, which is the same answer <c>project_overview</c> gives an agent.
    ///     The setting is read from the control database and handed to the reader, which is why this
    ///     composition lives here rather than in <c>Reading/</c>: an index is read from the index alone.
    /// </summary>
    public async Task<ProjectOverviewDetail> OverviewAsync(Project project, OverviewFilter filter,
        CancellationToken cancellationToken)
    {
        var patterns = await control.ExcludedPathsAsync(project.Slug, cancellationToken);
        var excluded = filter.ShowExcluded ? ExcludedPaths.None : new ExcludedPaths(patterns);
        return await readers.OverIndexAsync(project.Slug, filter.Repository,
            async (index, token) =>
            {
                var (overview, left, cards) = await index.LiveOverviewAsync(filter.Days, excluded,
                    _maxCommitPaths, token);
                return new ProjectOverviewDetail(overview, null, patterns.Count, left, cards);
            },
            problem => new ProjectOverviewDetail(null, problem.Explanation, patterns.Count), cancellationToken);
    }

    /// <summary>
    ///     Patterns proposed for the project's excluded paths from its index (#217), leaving out what the
    ///     setting already holds. Composed here for the reason <see cref="OverviewAsync" /> is: the
    ///     setting is the control database's and the proposals are the index's. Nothing is stored.
    /// </summary>
    public async Task<ExcludedPathSuggestionsDetail> SuggestExcludedPathsAsync(Project project,
        CancellationToken cancellationToken)
    {
        var patterns = await control.ExcludedPathsAsync(project.Slug, cancellationToken);
        return await readers.OverIndexAsync(project.Slug, null,
            async (index, token) =>
                new ExcludedPathSuggestionsDetail(await index.SuggestExcludedPathsAsync(patterns, logger, token), null),
            problem => new ExcludedPathSuggestionsDetail([], problem.Explanation), cancellationToken);
    }

    /// <summary>
    ///     Removes a project everywhere. The control database goes first, so nothing can start a clone
    ///     or a build against a project that is on its way out; a refresh already running relies on that
    ///     order too, since it counts discards before it looks the project up. A project already gone from the control
    ///     database — two operators deleting at once — still has its index and copies removed, which is
    ///     what the second one asked for too.
    /// </summary>
    public async Task DeleteAsync(Project project, CancellationToken cancellationToken)
    {
        await control.DeleteProjectAsync(project.Slug, cancellationToken);
        // Not the caller's token from here on: the project is already gone from the control database,
        // so a delete abandoned now would leave its index and durable copy behind for a project created
        // later under the slug to open (GHSA-253f-grfp-cqq7). The discard waits at most for a refresh's
        // publish and the drain, both bounded.
        await readers.DiscardAsync(project.Slug, CancellationToken.None);
        await clones.RemoveAsync(project.Slug, null, CancellationToken.None);
    }

    /// <summary>
    ///     Removes one repository and its local copy. Its files stay searchable until the next build,
    ///     which is why the project page reports the build time beside what the build found. False when
    ///     the project has no such repository.
    /// </summary>
    public async Task<bool> DeleteRepositoryAsync(Project project, string slug, CancellationToken cancellationToken)
    {
        if (!await control.DeleteRepositoryAsync(project.Slug, slug, cancellationToken)) return false;

        await clones.RemoveAsync(project.Slug, slug, cancellationToken);
        return true;
    }

    /// <param name="slug">The project to read.</param>
    /// <param name="restore">
    ///     Whether a project whose file is absent is restored from its durable copy first. True for one
    ///     project's own page, false for a read that walks every project (#9).
    /// </param>
    /// <param name="cancellationToken">Threaded through the restore and the queries.</param>
    private async Task<(ProjectIndexStatus Status, IReadOnlyList<IndexedRepository> Repositories)> ReadIndexAsync(
        string slug, bool restore, CancellationToken cancellationToken)
    {
        // Null is no index, and also a file left behind by an interrupted build: it reads as not built,
        // which is what it is and what the operator fixes by building again.
        var status = await readers.StatusAsync(slug, restore, cancellationToken);
        return status is null
            ? (ProjectIndexStatus.None, [])
            : (new ProjectIndexStatus(status.BuiltAt, status.FtsIndexed, status.Files, status.Lines),
                status.Repositories);
    }
}
