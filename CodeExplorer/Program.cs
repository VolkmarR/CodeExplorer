using System.ComponentModel;
using System.Globalization;
using System.Text;
using CodeExplorer;
using ModelContextProtocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// The default key ring (a folder under the user profile) protects stored credentials until #13
// moves it to Blob Storage and Key Vault; absent configuration must still yield a working server.
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ControlDatabase>();
builder.Services.AddSingleton<GitClones>();
builder.Services.AddSingleton<ProjectIndexes>();
builder.Services.AddSingleton<IndexBuilder>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<ProjectTools>();

var app = builder.Build();

// Operator endpoints, one group per module (ADR-0005).
var api = app.MapGroup("/api");
api.MapControl();
api.MapIndex();

// One MCP endpoint per project (ADR-0002). The filter binds the project from the route before the
// SDK sees the request, so every tool in the session answers for that project and nothing else.
// Once authentication arrives, the unknown-slug 404 moves to OnResourceMetadataRequest (ADR-0004)
// so protected-resource metadata is path-scoped as well; until then this filter stands in.
var projects = app.MapGroup("/projects/{slug}");
projects.AddEndpointFilter(async (context, next) =>
{
    string slug = (string)context.HttpContext.GetRouteValue("slug")!;
    var project = await context.HttpContext.RequestServices.GetRequiredService<ControlDatabase>()
        .FindAsync(slug, context.HttpContext.RequestAborted);
    if (project is null)
        return Results.NotFound(new
            { error = $"No project with slug '{slug}'. Create it with POST /api/projects first." });

    context.HttpContext.Items[ProjectTools.ProjectItemKey] = project;
    return await next(context);
});
projects.MapMcp("/mcp");

app.Run();

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

/// <summary>Marker so the tests can host the app through <c>WebApplicationFactory</c>.</summary>
public partial class Program;

[McpServerToolType]
internal sealed class ProjectTools(IHttpContextAccessor httpContextAccessor, ControlDatabase control, GitClones clones)
{
    public const string ProjectItemKey = "CodeExplorer.Project";

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
        var project = BoundProject();
        return $"This endpoint serves the project '{project.Name}' (slug: {project.Slug}).";
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
        var project = BoundProject();
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

    /// <summary>
    ///     The Streamable HTTP transport runs each handler inside the HTTP request that carried it, so
    ///     the project the route filter resolved is on the current context. If the SDK ever dispatches
    ///     off-request this fails loudly rather than binding to nothing.
    /// </summary>
    private Project BoundProject() =>
        httpContextAccessor.HttpContext?.Items[ProjectItemKey] as Project
        ?? throw new McpException("No project is bound to this request. Connect through /projects/{slug}/mcp.");
}
