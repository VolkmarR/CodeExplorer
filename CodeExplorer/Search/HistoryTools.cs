using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The MCP tools that answer from a project's history (CONTEXT.md): what changed, who changed a
///     file, which commit each line was last changed by, and what one commit did. They live beside the
///     other index-backed tools because that is what they are — history is tables in the project index like any other,
///     and none of these ever opens a local copy or talks to a remote.
///     What each one does is <see cref="HistoryQueries" />'s, which the operator's pages ask the same
///     questions of; what is left here is the reply an agent reads. That split is the point: a ranking
///     and a page of the same window cannot disagree about what a window is when only one place
///     decides, and the sentences below are the half a JSON response has no use for.
///     Every one of them is careful about the same thing: attribution says who touched a line last and
///     not who wrote the logic (CONTEXT.md, Attribution). An agent that reads it as authorship will
///     confidently name the person who reformatted the file, so each tool says so in its own words
///     rather than relying on the agent having read another one's.
/// </summary>
[McpServerToolType]
internal sealed partial class HistoryTools(IHttpContextAccessor httpContextAccessor, HistoryQueries history)
{
    private const int DefaultCommits = 30;

    /// <summary>
    ///     Authors named by default. The overview stops at ten, which answers "who to ask"; this answers
    ///     who has been here at all, so it is a page of a team rather than its top.
    /// </summary>
    private const int DefaultAuthors = 30;

    /// <summary>
    ///     A whole mid-sized file's blame in one call. Runs, not lines, so this is far more of a file
    ///     than the number suggests — a 2000-line file is usually well under a hundred runs.
    /// </summary>
    private const int MaxBlameRuns = 400;

    private Project Bound => BoundProject.Get(httpContextAccessor);

    /// <summary>
    ///     What a scope with no commit at all is answered with, when the project around it has history.
    ///     That is a repository whose walk found nothing, and it reads as "nobody has changed it" unless
    ///     it is said outright (CONTEXT.md, History) — so both rankings say it, and say it the same way,
    ///     because two spellings of one fact are two facts to keep in step.
    /// </summary>
    /// <param name="spelled">What the reply calls the scope, so the answer and the question match.</param>
    /// <param name="projectSlug">The project the scope belongs to.</param>
    private static string NoCommitsIn(string spelled, string projectSlug) =>
        $"No commit is recorded for {spelled}, although project '{projectSlug}' has history for other "
        + "repositories. Its history could not be walked; git_log without a scope shows what was imported.";

    /// <summary>
    ///     The caveat naming the repositories a ranking cannot speak for, or null when there are none.
    ///     Which repositories those are is <see cref="IndexReader.HistoryCoverageAsync" />'s to decide;
    ///     this only says it in the words a tool reply uses.
    /// </summary>
    private static string? Coverage(HistoryCoverage coverage) =>
        coverage.Without.Count == 0
            ? null
            : $"History was imported for {string.Join(", ", coverage.With)} and for none of "
              + $"{string.Join(", ", coverage.Without)}, so nothing from those can appear above however "
              + "much they changed.";

    private static void Append(StringBuilder text, RecordedChange commit, bool withRepository)
    {
        text.Append(CultureInfo.InvariantCulture,
            $"{commit.Sha[..8]}  {commit.AuthoredAt:yyyy-MM-dd}  {commit.AuthorName} <{commit.AuthorEmail}>");
        // The repository is named only when the answer spans several, so a single-repository project
        // and a scoped call do not repeat one slug down the whole reply.
        if (withRepository) text.Append(CultureInfo.InvariantCulture, $"  [{commit.RepositorySlug}]");
        text.Append(CultureInfo.InvariantCulture, $"\n            {ToolReply.Clip(commit.Subject)}\n");
    }

    private static string Scope(IndexedRepository? repository) =>
        repository is { } found ? $" in repository '{found.Slug}'" : "";

    /// <summary>
    ///     The same, where a <c>path</c> scope may have narrowed the read further (#118). The path wins
    ///     because it is the narrower of the two and names the repository already; naming both would
    ///     say the same slug twice in one sentence.
    /// </summary>
    private static string Scope(IndexedRepository? repository, PathScope? path) =>
        path is { } under ? $" under '{under.Spelled}'" : Scope(repository);
}
