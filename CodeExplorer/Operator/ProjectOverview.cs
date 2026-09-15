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
    long? LineCount);

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

    /// <summary>Null when there is no such project, which the endpoint turns into a 404.</summary>
    public async Task<ProjectDetail?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        if (await control.FindAsync(slug, cancellationToken) is not { } project) return null;

        var configured = await control.ListRepositoriesAsync(slug, cancellationToken);
        // One project's page restores that one project, which is the whole of what lazy attach asks:
        // a deliberate visit to a project pays for it, and the list above does not pay for all of them.
        var (status, indexed) = await ReadIndexAsync(slug, true, cancellationToken);
        // Driven by the configured repositories, not the indexed ones: a repository removed since the
        // last build is gone from the page immediately, even though its files are still searchable.
        var repositories = configured
            .Select(r => indexed.GetValueOrDefault(r.Slug) is { } built
                ? new RepositoryDetail(r.Slug, r.Url, r.HasCredential, built.HeadCommit, built.FileCount,
                    built.LineCount)
                : new RepositoryDetail(r.Slug, r.Url, r.HasCredential, null, null, null))
            .ToList();
        return new ProjectDetail(project.Slug, project.Name, project.SingleRepository, status, repositories);
    }

    /// <summary>
    ///     Removes a project everywhere. The control database goes first, so nothing can start a clone
    ///     or a build against a project that is on its way out. False when there was no such project.
    /// </summary>
    public async Task<bool> DeleteAsync(string slug, CancellationToken cancellationToken)
    {
        if (!await control.DeleteProjectAsync(slug, cancellationToken)) return false;

        await indexes.DiscardAsync(slug, cancellationToken);
        await clones.RemoveAsync(slug, null, cancellationToken);
        return true;
    }

    /// <summary>
    ///     Removes one repository and its local copy. Its files stay searchable until the next build,
    ///     which is why the project page reports the build time beside what the build found.
    /// </summary>
    public async Task<bool> DeleteRepositoryAsync(string project, string slug, CancellationToken cancellationToken)
    {
        if (!await control.DeleteRepositoryAsync(project, slug, cancellationToken)) return false;

        await clones.RemoveAsync(project, slug, cancellationToken);
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
