namespace CodeExplorer;

/// <summary>
///     A qualified path (CONTEXT.md) taken apart: which repository, and where inside it. How it is
///     written back out depends on the project, so it is not written here — <see cref="ProjectPaths" />
///     both parses and formats, and is the only thing that constructs this.
/// </summary>
public sealed record QualifiedPath(string RepositorySlug, string PathInRepository);

/// <summary>
///     How one project names its files, and how it explains that naming to an agent that got it
///     wrong. A path cannot be read or written without this: <c>src/index.ts</c> is a file in a
///     single-repository project and a repository named <c>src</c> in any other, and only the project
///     says which (ADR-0006).
/// </summary>
public sealed record ProjectPaths(bool SingleRepository, string RepositorySlug)
{
    /// <summary>
    ///     The shape declared for the project, anchored to the repository it names things after. An
    ///     index with no repositories in it yet falls back to the project slug, which nothing will match
    ///     but which keeps every message readable.
    /// </summary>
    public static ProjectPaths For(Project project, IReadOnlyList<ProjectRepository> repositories) =>
        new(project.SingleRepository, repositories.Count > 0 ? repositories[0].Slug : project.Slug);

    /// <summary>
    ///     Null means the repository level: the root of a multi-repository project, which names no file
    ///     and no repository. A single-repository project has no such level, so the empty path is its
    ///     one repository's own top level and null never comes back for it. Separators are normalised so
    ///     an agent may write either slash.
    /// </summary>
    public QualifiedPath? Parse(string path)
    {
        string normalized = path.Trim().Replace('\\', '/').Trim('/');
        if (SingleRepository) return new QualifiedPath(RepositorySlug, normalized);

        if (normalized.Length == 0) return null;
        int slash = normalized.IndexOf('/');
        return slash < 0
            ? new QualifiedPath(normalized, "")
            : new QualifiedPath(normalized[..slash], normalized[(slash + 1)..]);
    }

    /// <summary>
    ///     Back to the string an agent quotes and the index stores. The slug leads it only where the
    ///     project puts it there, so a path is never printed in the other project's shape.
    /// </summary>
    public string Format(QualifiedPath path)
    {
        if (SingleRepository) return path.PathInRepository;
        return path.PathInRepository.Length == 0
            ? path.RepositorySlug
            : $"{path.RepositorySlug}/{path.PathInRepository}";
    }

    /// <summary>Joins a repository-relative path onto this shape, for a path built rather than parsed.</summary>
    public string Format(string repositorySlug, string pathInRepository) =>
        Format(new QualifiedPath(repositorySlug, pathInRepository));

    /// <summary>The shape a path should have been written in, for the message that says it was not.</summary>
    public string Example() => Format(RepositorySlug, "src/File.cs");
}
