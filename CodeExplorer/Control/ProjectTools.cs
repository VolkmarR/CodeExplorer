using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The MCP tools about the project itself (ADR-0005, <c>Control/</c>): which one this endpoint
///     serves, what repositories it has and how they were indexed, and their committed trees. They are
///     the only tools allowed to read both the control database and the index, because "configured but
///     not indexed yet" is a question neither side can answer alone.
/// </summary>
[McpServerToolType]
internal sealed class ProjectTools(
    IHttpContextAccessor httpContextAccessor, ControlDatabase control, GitClones clones, ProjectIndexes indexes)
{
    /// <summary>
    ///     Enough to show a whole mid-sized tree in one call while keeping a runaway `depth` on a large
    ///     monorepo from returning megabytes; the agent is told how to narrow down.
    /// </summary>
    private const int MaxEntries = 2000;

    [McpServerTool(Name = "which_project")]
    [Description(
        "Reports which project this MCP endpoint is bound to. The project comes from the URL you connected to, not from an argument; use it to confirm the server before searching.")]
    public string WhichProject()
    {
        var project = BoundProject.Get(httpContextAccessor);
        return $"This endpoint serves the project '{project.Name}' (slug: {project.Slug}).";
    }

    [McpServerTool(Name = "repo_info", ReadOnly = true, Idempotent = true, Title = "Describe the project's index")]
    [Description("""
                 Describes this project: its repositories with the commit each was indexed at, file and line counts, when the index was built and whether full-text search is active. Call it first when you do not know what the project holds, then list_tree(depth=2) to learn the layout. A repository added after the last build is listed as not indexed yet, so a missing file may be waiting for a rebuild rather than absent.
                 """)]
    public async Task<string> RepoInfo(CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        var configured = await control.ListRepositoriesAsync(project.Slug, cancellationToken);

        using var index = await FileQueries.OpenAsync(indexes, project.Slug, cancellationToken);
        if (index is null)
            return ToolReply.NoIndex(project.Slug) + (configured.Count == 0
                ? $" The project has no repositories yet either; add one with POST /api/projects/{project.Slug}/repositories."
                : $" Repositories waiting to be indexed: {string.Join(", ", configured.Select(r => r.Slug))}.");

        var info = await index.InfoAsync(cancellationToken);
        var indexed = await index.RepositoriesAsync(cancellationToken);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Project '{project.Slug}' ({project.Name}): {indexed.Count} {ToolReply.Plural(indexed.Count, "repository", "repositories")} indexed, {indexed.Sum(r => r.FileCount)} files, {indexed.Sum(r => r.LineCount)} lines.\n");
        // One time for the project: a refresh rebuilds every repository together (CONTEXT.md), so there
        // is no per-repository index time to report.
        text.Append(CultureInfo.InvariantCulture,
            $"Indexed at: {info.BuiltAt:yyyy-MM-dd HH:mm:ss} UTC (all repositories are indexed together)\n");
        text.Append(info.FtsIndexed
            ? "Full-text index: built (text queries in grep use BM25 over identifier tokens)\n"
            : "Full-text index: not built (text queries in grep use a substring scan)\n");

        text.Append("\nRepositories:\n");
        int width = Math.Max(indexed.Select(r => r.Slug.Length).DefaultIfEmpty(0).Max(),
            configured.Select(r => r.Slug.Length).DefaultIfEmpty(0).Max());
        foreach (var repository in indexed)
            text.Append(repository.Slug.PadRight(width)).Append(CultureInfo.InvariantCulture,
                $"  {repository.FileCount} {ToolReply.Plural(repository.FileCount, "file")}, {repository.LineCount} {ToolReply.Plural(repository.LineCount, "line")}, commit {repository.HeadCommit[..Math.Min(12, repository.HeadCommit.Length)]}, {repository.Url}\n");
        foreach (var repository in configured.Where(c => indexed.All(i => i.Slug != c.Slug)))
            text.Append(repository.Slug.PadRight(width)).Append(CultureInfo.InvariantCulture,
                $"  not indexed yet: added after the last build. The operator includes it with POST /api/projects/{project.Slug}/index.\n");

        return text.ToString();
    }

    [McpServerTool(Name = "list_tree")]
    [Description(
        "Lists directories and files of this project, like `tree -L depth`. Paths are qualified: the first segment is the repository slug, the rest is the path inside that repository (`main/src/Lib`). An empty path lists the repositories, and with depth 2 or more their top-level entries as well. Directories end with `/`. Entries come from the committed HEAD tree, so there is no working copy and no .gitignore filtering. The first call for a repository clones it, which can take a few seconds.")]
    public async Task<string> ListTree(
        [Description("Qualified path of the directory to list: `repo` or `repo/dir/sub`. Empty for the project root.")]
        string path = "",
        [Description("How many levels to descend, at least 1. Default 1 lists only direct children.")]
        int depth = 1,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        if (depth < 1)
            return "depth must be at least 1. Use 1 for direct children, 2 to include grandchildren, and so on.";

        var repositories = await control.ListRepositoriesAsync(project.Slug, cancellationToken);
        if (repositories.Count == 0)
            return
                $"Project '{project.Slug}' has no repositories yet. Ask the operator to add one with POST /api/projects/{project.Slug}/repositories.";

        var qualified = QualifiedPath.Parse(path);
        if (qualified is null) return await ListRootAsync(project, repositories, depth, cancellationToken);

        var repository = repositories.FirstOrDefault(r => r.Slug == qualified.RepositorySlug);
        if (repository is null)
            return
                $"No repository '{qualified.RepositorySlug}' in project '{project.Slug}'. Repositories: {string.Join(", ", repositories.Select(r => r.Slug))}. "
                + "The first path segment must be one of these.";

        using var repo = await clones.OpenAsync(repository, cancellationToken);
        if (!GitClones.HasCommits(repo))
            return $"Repository '{repository.Slug}' has no commits yet, so there is nothing to list.";
        if (clones.DeclaresLfs(repo)) return $"Repository '{repository.Slug}': {GitClones.LfsRefusal}";

        var entries = GitClones.ListTree(repo, qualified.PathInRepository, depth);
        if (entries is null)
            return
                $"'{qualified.PathInRepository}' is not a directory in repository '{repository.Slug}'. Call list_tree with a parent path to see what exists there.";

        var text = new StringBuilder($"{qualified}/ (depth {depth}, {entries.Count} entries)\n");
        AppendEntries(text, "", entries);
        return text.ToString();
    }

    /// <summary>
    ///     The root lists every repository and, for depth 2 or more, their trees one level shallower. A
    ///     repository that cannot be listed (LFS, clone failure) gets its reason inline so one bad
    ///     repository does not hide the others.
    /// </summary>
    private async Task<string> ListRootAsync(
        Project project, IReadOnlyList<ProjectRepository> repositories, int depth, CancellationToken cancellationToken)
    {
        var text = new StringBuilder($"{project.Slug} (depth {depth}, {repositories.Count} repositories)\n");
        foreach (var repository in repositories)
        {
            text.Append(repository.Slug).Append("/\n");
            if (depth == 1) continue;

            try
            {
                using var repo = await clones.OpenAsync(repository, cancellationToken);
                if (!GitClones.HasCommits(repo)) continue;
                if (clones.DeclaresLfs(repo))
                {
                    text.Append("  ").Append(GitClones.LfsRefusal).Append('\n');
                    continue;
                }

                AppendEntries(text, repository.Slug + "/", GitClones.ListTree(repo, "", depth - 1)!);
            }
            catch (McpException ex)
            {
                // Safe to swallow: the failure is reported in place of this repository's entries, and
                // the other repositories still list. A direct call on the repository rethrows it.
                text.Append("  ").Append(ex.Message).Append('\n');
            }
        }

        return text.ToString();
    }

    private static void AppendEntries(StringBuilder text, string prefix, IReadOnlyList<TreeEntryInfo> entries)
    {
        foreach (var entry in entries.Take(MaxEntries))
            text.Append(prefix).Append(entry.RelativePath).Append(entry.IsDirectory ? "/\n" : "\n");
        if (entries.Count > MaxEntries)
            text.Append(CultureInfo.InvariantCulture,
                $"... {entries.Count - MaxEntries} more entries omitted. List a subdirectory or use a smaller depth.\n");
    }
}
