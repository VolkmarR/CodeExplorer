namespace CodeExplorer;

/// <summary>
///     A qualified path (CONTEXT.md): the repository slug, then the path inside that repository — or,
///     in a single-repository project, the path inside its one repository alone (ADR-0006).
///     <see cref="ShowsRepository" /> says which shape this one is written in, so a path that came
///     from one project is never printed in the other's shape.
/// </summary>
internal sealed record QualifiedPath(string RepositorySlug, string PathInRepository, bool ShowsRepository)
{
    public override string ToString()
    {
        if (!ShowsRepository) return PathInRepository;
        return PathInRepository.Length == 0 ? RepositorySlug : $"{RepositorySlug}/{PathInRepository}";
    }
}

/// <summary>
///     How one project names its files, and how it explains that naming to an agent that got it
///     wrong. A path cannot be parsed without this: <c>src/index.ts</c> is a file in a
///     single-repository project and a repository named <c>src</c> in any other, and only the project
///     says which (ADR-0006).
/// </summary>
internal sealed record ProjectPaths(bool SingleRepository, string RepositorySlug)
{
    public static ProjectPaths For(Project project, IReadOnlyList<ProjectRepository> repositories) =>
        new(project.SingleRepository, repositories.Count > 0 ? repositories[0].Slug : project.Slug);

    /// <summary>
    ///     Null for the project root, which names no file and, in a multi-repository project, no
    ///     repository either. Separators are normalised so an agent may write either slash.
    /// </summary>
    public QualifiedPath? Parse(string path)
    {
        string normalized = path.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return null;

        // Nothing to split off: the whole string is the path inside the one repository, whose slug the
        // caller never wrote and never sees.
        if (SingleRepository) return new QualifiedPath(RepositorySlug, normalized, false);

        int slash = normalized.IndexOf('/');
        return slash < 0
            ? new QualifiedPath(normalized, "", true)
            : new QualifiedPath(normalized[..slash], normalized[(slash + 1)..], true);
    }

    /// <summary>The shape a path should have been written in, for the message that says it was not.</summary>
    public string Example(string exampleSlug) =>
        SingleRepository ? "src/File.cs" : $"{exampleSlug}/src/File.cs";
}
