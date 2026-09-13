namespace CodeExplorer;

/// <summary>
///     A qualified path (CONTEXT.md): the repository slug, then the path inside that repository.
///     Separators are normalised so an agent may write either slash.
/// </summary>
internal sealed record QualifiedPath(string RepositorySlug, string PathInRepository)
{
    /// <summary>Null for the project root, which names no repository.</summary>
    public static QualifiedPath? Parse(string path)
    {
        string normalized = path.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return null;
        int slash = normalized.IndexOf('/');
        return slash < 0
            ? new QualifiedPath(normalized, "")
            : new QualifiedPath(normalized[..slash], normalized[(slash + 1)..]);
    }

    public override string ToString() =>
        PathInRepository.Length == 0 ? RepositorySlug : $"{RepositorySlug}/{PathInRepository}";
}
