using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The MCP tools about the project itself (ADR-0005, <c>Control/</c>): which one this endpoint
///     serves, what repositories it has and how they were indexed. They are the only tools allowed to
///     read both the control database and the index, because "configured but not indexed yet" is a
///     question neither side can answer alone. Nothing here opens a local copy: a tool answers from
///     the index (CONTEXT.md), and <c>list_tree</c> lives with the other index readers in
///     <c>Search/</c>.
/// </summary>
[McpServerToolType]
internal sealed class ProjectTools(
    IHttpContextAccessor httpContextAccessor,
    ControlDatabase control,
    IndexReaders readers)
{
    /// <summary>The project this call is bound to, read the way every tool class here reads it.</summary>
    private Project Bound => BoundProject.Get(httpContextAccessor);

    [McpServerTool(Name = "which_project")]
    [Description(
        "Reports which project this MCP endpoint is bound to. The project comes from the URL you connected to, not from an argument; use it to confirm the server before searching.")]
    public string WhichProject()
    {
        return $"This endpoint serves the project '{Bound.Name}' (slug: {Bound.Slug}).";
    }

    [McpServerTool(Name = "repo_info", ReadOnly = true, Idempotent = true, Title = "Describe the project's index")]
    [Description("""
                 Describes this project: its repositories with the commit each was indexed at, file and line counts, how much history each holds and the dates it spans, when the index was built and whether full-text search is active. Call it first when you do not know what the project holds, then list_tree(depth=2) to learn the layout. A repository added after the last build is listed as not indexed yet, so a missing file may be waiting for a rebuild rather than absent.

                 - The history line answers "how far back does this go" and "how much is there" without paging git_log for either. The span is of what was imported, which may begin later than the repository itself does.
                 - A repository whose history was never walked says so. That is not the same as a repository nobody has changed, and no history tool can answer anything about it.
                 """)]
    public async Task<string> RepoInfo(CancellationToken cancellationToken = default)
    {
        var configured = await control.ListRepositoriesAsync(Bound.Slug, cancellationToken);

        // Null covers a build that was interrupted while filling the file as well as a project never
        // built: the reader will not report half-built tables as the project, and either way the
        // agent's next move is the same.
        var status = await readers.StatusAsync(Bound.Slug, true, cancellationToken);
        if (status is null)
            return IndexReader.NoIndex(Bound.Slug) + (configured.Count == 0
                ? $" The project has no repositories yet either; add one with POST /api/projects/{Bound.Slug}/repositories."
                : $" Repositories waiting to be indexed: {string.Join(", ", configured.Select(r => r.Slug))}.");

        var indexed = status.Repositories;

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Project '{Bound.Slug}' ({Bound.Name}): {indexed.Count} {ToolReply.Plural(indexed.Count, "repository", "repositories")} indexed, {status.Files} files, {status.Lines} lines.\n");
        // One time for the project: a refresh rebuilds every repository together (CONTEXT.md), so there
        // is no per-repository index time to report.
        text.Append(CultureInfo.InvariantCulture,
            $"Indexed at: {status.BuiltAt:yyyy-MM-dd HH:mm:ss} UTC (all repositories are indexed together)\n");
        text.Append(status.FtsIndexed
            ? "Tokenised: yes (text queries in grep match whole identifier tokens)\n"
            : "Tokenised: no (text queries in grep use a substring scan, which also finds partial names)\n");

        text.Append("\nRepositories:\n");
        int width = Math.Max(indexed.Select(r => r.Slug.Length).DefaultIfEmpty(0).Max(),
            configured.Select(r => r.Slug.Length).DefaultIfEmpty(0).Max());
        foreach (var repository in indexed)
        {
            text.Append(repository.Slug.PadRight(width)).Append(CultureInfo.InvariantCulture,
                $"  {repository.FileCount} {ToolReply.Plural(repository.FileCount, "file")}, {repository.LineCount} {ToolReply.Plural(repository.LineCount, "line")}, commit {repository.HeadCommit[..Math.Min(12, repository.HeadCommit.Length)]}, {repository.Url}\n");
            text.Append(' ', width).Append("  ").Append(History(repository)).Append('\n');
        }

        foreach (var repository in configured.Where(c => indexed.All(i => i.Slug != c.Slug)))
            text.Append(repository.Slug.PadRight(width)).Append(CultureInfo.InvariantCulture,
                $"  not indexed yet: added after the last refresh. The operator includes it with POST /api/projects/{Bound.Slug}/refresh.\n");

        return text.ToString();
    }

    /// <summary>
    ///     How much history one repository has, and how far back it reaches. Said here because the
    ///     alternative is what an agent did instead: bisecting `git_log(limit=1, page=N)` until the
    ///     oldest commit turned up, which cost one measured evaluation fifteen of its twenty-eight
    ///     calls to establish two dates (#108).
    ///     A repository with none says so in those words. An empty span and a repository nobody has
    ///     changed read alike and mean opposite things (CONTEXT.md, History) — and here the first is
    ///     always what it means, because a walk that ran records the root commit at least.
    /// </summary>
    private static string History(IndexedRepository repository)
    {
        if (repository.Commits == 0 || repository.FirstCommitAt is not { } first ||
            repository.LastCommitAt is not { } last)
            return "no history imported: its walk found nothing, or the index predates history. "
                   + "Nothing about who changed what can be answered for it.";

        // One interpolated string and not a concatenation: joined with `+` it is a string rather than a
        // handler, and the culture-aware overload then binds to something else entirely.
        return string.Create(CultureInfo.InvariantCulture,
            $"{repository.Commits:N0} {ToolReply.Plural(repository.Commits, "commit")} imported, {first:yyyy-MM-dd} to {last:yyyy-MM-dd}. That is what was imported, which may begin later than the repository itself does.");
    }

    [McpServerTool(Name = "project_overview", ReadOnly = true, Idempotent = true,
        Title = "Describe the whole project in one call")]
    [Description("""
                 Describes the shape of this project in one call: its repositories and the commit each is at, how much of it is written in which language, the top level of each repository with sizes, the largest files, the files that changed most recently, and the people who have touched it most. Call it first, before any list_tree or glob, when you do not yet know what this project is.

                 - It answers from the index as the last refresh built it, not from a live scan, so it costs one read however large the project is.
                 - Counts are by language where a file's extension is one this server knows, and by extension where it is not. An extension standing for itself is a weaker claim than a language name, and is printed as one.
                 - The most-changed section covers a window ending at the newest commit in the index, not at today, and says which dates those are. A project whose history was never imported says so instead of showing nothing.
                 - Authors are who touched the code last, never who wrote it: a reformat is a change and it becomes the answer.
                 """)]
    public async Task<string> ProjectOverview(CancellationToken cancellationToken = default)
    {
        // The repositories come from the index's own table rather than from the stored row: they are
        // already one join-free read, and a second copy inside the overview would be a second
        // definition of what this project holds (IndexOverview says the same).
        return await readers.OverIndexAsync(Bound.Slug, null,
            async (index, token) => OverviewReply.Render(Bound, await index.RepositoriesAsync(token),
                await index.OverviewAsync(token)),
            problem => problem.Explanation, cancellationToken);
    }
}
