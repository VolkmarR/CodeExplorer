namespace CodeExplorer;

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
///     What the project page draws beside its repositories: the same overview an agent is given, from
///     the same row, so an operator looking at a project sees what an agent sees.
///     Exactly one of the two is set. <see cref="Unavailable" /> is the reader's own prose saying why
///     there is nothing to show — a project never built, or one whose first build is still running —
///     and it is carried rather than collapsed to null so that the page says what an agent asking the
///     same question is told, instead of showing an operator a blank where there is an explanation.
///     It is its own response rather than a field on <see cref="ProjectDetail" /> because it is the
///     one read on this page that restores a durable copy, and the page's header must not wait behind it.
/// </summary>
public sealed record ProjectOverviewDetail(IndexOverview? Overview, string? Unavailable);

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
public sealed class ProjectOverview(ControlDatabase control, ProjectIndexes indexes, GitClones clones)
{
    /// <summary>
    ///     One index read per project. An operator administers projects by hand, so the list is the
    ///     length of a screen and a single query joining across attached databases would buy nothing.
    /// </summary>
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var projects = await control.ListAsync(cancellationToken);
        var summaries = new List<ProjectSummary>(projects.Count);
        foreach (var project in projects)
        {
            var repositories = await control.ListRepositoriesAsync(project.Slug, cancellationToken);
            // Peeked, not opened: the list touches every project, and restoring every durable copy to
            // draw one page is exactly what lazy attach exists to avoid (#9). A project whose file the
            // last shutdown wiped reads as not indexed here until someone opens it.
            var (status, _) = await ReadIndexAsync(project.Slug, false, cancellationToken);
            summaries.Add(new ProjectSummary(project.Slug, project.Name, project.SingleRepository, repositories.Count,
                status));
        }

        return summaries;
    }

    /// <summary>One project's page. The project is the route's, so there is no "none" to answer here.</summary>
    public async Task<ProjectDetail> FindAsync(Project project, CancellationToken cancellationToken)
    {
        var configured = await control.ListRepositoriesAsync(project.Slug, cancellationToken);
        // One project's page restores that one project, which is the whole of what lazy attach asks:
        // a deliberate visit to a project pays for it, and the list above does not pay for all of them.
        var (status, indexed) = await ReadIndexAsync(project.Slug, true, cancellationToken);
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
    ///     The overview stored with one project's index (#51). A project with no index is not a failure
    ///     here — every project passes through that state — so the reader's refusal is carried through
    ///     as prose rather than raised, which is the same answer <c>project_overview</c> gives an agent.
    /// </summary>
    public async Task<ProjectOverviewDetail> OverviewAsync(Project project, CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, project.Slug, null, cancellationToken);
        if (open is IndexOpen.Refused refused) return new ProjectOverviewDetail(null, refused.Explanation);

        using var index = ((IndexOpen.Opened)open).Reader;
        var overview = await index.OverviewAsync(cancellationToken);
        // A built index with no overview row is the one case neither side can explain from what it
        // holds, so it is named here in the words the tool uses: the index came from another build.
        return overview is null
            ? new ProjectOverviewDetail(null, IndexReader.NoOverview(project.Slug))
            : new ProjectOverviewDetail(overview, null);
    }

    /// <summary>
    ///     Removes a project everywhere. The control database goes first, so nothing can start a clone
    ///     or a build against a project that is on its way out. A project already gone from the control
    ///     database — two operators deleting at once — still has its index and copies removed, which is
    ///     what the second one asked for too.
    /// </summary>
    public async Task DeleteAsync(Project project, CancellationToken cancellationToken)
    {
        await control.DeleteProjectAsync(project.Slug, cancellationToken);
        await indexes.DiscardAsync(project.Slug, cancellationToken);
        await clones.RemoveAsync(project.Slug, null, cancellationToken);
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
    private async Task<(ProjectIndexStatus Status, Dictionary<string, IndexedRepository> Indexed)> ReadIndexAsync(
        string slug, bool restore, CancellationToken cancellationToken)
    {
        // Null is no index, and also a file left behind by an interrupted build: it reads as not built,
        // which is what it is and what the operator fixes by building again.
        var status = await IndexReader.StatusAsync(indexes, slug, restore, cancellationToken);
        return status is null
            ? (ProjectIndexStatus.None, [])
            : (new ProjectIndexStatus(status.BuiltAt, status.FtsIndexed, status.Files, status.Lines),
                status.Repositories.ToDictionary(r => r.Slug));
    }
}
