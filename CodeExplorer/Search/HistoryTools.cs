using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The MCP tools that answer from a project's history (CONTEXT.md): what changed, who changed a
///     file, and which commit each line was last changed by. They live beside the other index-backed
///     tools because that is what they are — history is tables in the project index like any other,
///     and none of these ever opens a local copy or talks to a remote.
///     Every one of them is careful about the same thing: attribution says who touched a line last and
///     not who wrote the logic (CONTEXT.md, Attribution). An agent that reads it as authorship will
///     confidently name the person who reformatted the file, so each tool says so in its own words
///     rather than relying on the agent having read another one's.
/// </summary>
[McpServerToolType]
internal sealed class HistoryTools(IHttpContextAccessor httpContextAccessor, ProjectIndexes indexes)
{
    private const int DefaultCommits = 30;

    private const int MaxCommits = 200;

    /// <summary>
    ///     A whole mid-sized file's blame in one call. Runs, not lines, so this is far more of a file
    ///     than the number suggests — a 2000-line file is usually well under a hundred runs.
    /// </summary>
    private const int MaxBlameRuns = 400;

    /// <summary>
    ///     What every tool here says when the project has no history. Distinguishing this from "nothing
    ///     matched" is the whole point: an empty answer to "who changed this" reads as "nobody", which is
    ///     a fact, and this is the absence of one.
    /// </summary>
    private const string NoHistory =
        "This project's index holds no history, so no commit, author or date can be reported for it. "
        + "That is the case for an index built before history was imported, and for one whose repositories "
        + "could not be walked. Ask the operator to refresh the project; the code itself is searchable meanwhile.";

    [McpServerTool(Name = "git_log", ReadOnly = true, Idempotent = true, Title = "List the project's commits")]
    [Description("""
                 Lists commits of the project's default branch, newest first. Use it to see what changed recently, or to find the commit an agent should ask about next.

                 - Merges count as one commit and their side branches are not walked, so a pull request reads as a single change.
                 - Only the default branch is recorded. A commit on a branch that was never merged is not here.
                 - History may not reach the beginning of the repository, and it is not the same as the code: use file_history for one file, and blame for one line.
                 """)]
    public async Task<string> GitLog(
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repository = null,
        [Description("Commits to return, 1-200. Default 30.")]
        int limit = DefaultCommits,
        [Description("1-based page of results, newest first.")]
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var open = await IndexReader.OpenAsync(indexes, BoundProject.Get(httpContextAccessor).Slug, repository,
            cancellationToken);
        if (open is IndexOpen.Refused refused) return refused.Explanation;
        using var index = ((IndexOpen.Opened)open).Reader;
        if (!await index.HasHistoryAsync(cancellationToken)) return NoHistory;

        limit = Math.Clamp(limit, 1, MaxCommits);
        int skip = (Math.Max(1, page) - 1) * limit;
        var commits = await index.CommitsAsync(index.Repository?.Slug, limit, skip, cancellationToken);
        if (commits.Count == 0)
            return skip > 0
                ? $"No commits on page {page}. There are fewer than {skip + 1} commits recorded{Scope(index)}."
                : $"No commits are recorded{Scope(index)}.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{commits.Count} {ToolReply.Plural(commits.Count, "commit")}{Scope(index)}, newest first:\n\n");
        foreach (var commit in commits) Append(text, commit, index.Repository is null);
        return ToolReply.Cap(text.ToString(), $"Narrow with repository, or raise page past {page}.");
    }

    [McpServerTool(Name = "file_history", ReadOnly = true, Idempotent = true,
        Title = "List the commits that changed a file")]
    [Description("""
                 Lists the commits that changed one file, newest first. This is the tool for "who has worked on this" and "when was this last touched".

                 - The path is qualified, exactly as grep and read_file print it: `main/src/Api/Foo.cs`.
                 - History is matched by path, so it begins where the file was last renamed or moved. An empty or short answer on an old file usually means a move, not that nobody touched it — the content's line-by-line history survives a move and is what blame reports.
                 - It says who changed the file and when, never what they changed: the diffs are not indexed.
                 """)]
    public async Task<string> FileHistory(
        [Description("Qualified path of one file, e.g. \"main/src/Api/Foo.cs\".")]
        string path,
        [Description("Commits to return, 1-200. Default 30.")]
        int limit = DefaultCommits,
        CancellationToken cancellationToken = default)
    {
        var open = await IndexReader.OpenAsync(indexes, BoundProject.Get(httpContextAccessor).Slug, null,
            cancellationToken);
        if (open is IndexOpen.Refused refused) return refused.Explanation;
        using var index = ((IndexOpen.Opened)open).Reader;
        if (!await index.HasHistoryAsync(cancellationToken)) return NoHistory;

        var located = await LocateAsync(index, path, cancellationToken);
        if (located.Problem is not null) return located.Problem;

        var commits = await index.FileHistoryAsync(located.RepositorySlug!, located.PathInRepository!,
            Math.Clamp(limit, 1, MaxCommits), cancellationToken);
        if (commits.Count == 0)
            return $"No commit in the recorded history changed '{located.Spelled}'. "
                   + "The file is in the index, so this means its history is older than what was imported, or it "
                   + "reached this path by a rename — try blame, which follows the content rather than the path.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{commits.Count} {ToolReply.Plural(commits.Count, "commit")} changed {located.Spelled}, newest first:\n\n");
        foreach (var commit in commits) Append(text, commit, false);
        return ToolReply.Cap(text.ToString(), "Lower limit to see fewer.");
    }

    [McpServerTool(Name = "blame", ReadOnly = true, Idempotent = true, Title = "Show which commit last changed each line")]
    [Description("""
                 Shows which commit last changed each line of a file, grouped into runs of consecutive lines. Use it after a grep to find out who to ask about a specific line.

                 - This is who touched a line LAST, not who wrote the logic. A reformat, a rename or a whitespace fix is a change, and it becomes the answer. Treat a result as "ask this person", never as "this person introduced it".
                 - It cannot say when something was introduced: only the current content is indexed, so a line that was moved or reformatted points at that change and not at the original one.
                 - Lines with no commit are ones the build could not attribute; they are reported as such rather than left out.
                 """)]
    public async Task<string> Blame(
        [Description("Qualified path of one file, e.g. \"main/src/Api/Foo.cs\".")]
        string path,
        [Description("1-based first line. Default 1.")]
        int startLine = 1,
        [Description("Last line, inclusive. Default: the end of the file.")]
        int? endLine = null,
        CancellationToken cancellationToken = default)
    {
        var open = await IndexReader.OpenAsync(indexes, BoundProject.Get(httpContextAccessor).Slug, null,
            cancellationToken);
        if (open is IndexOpen.Refused refused) return refused.Explanation;
        using var index = ((IndexOpen.Opened)open).Reader;
        if (!await index.HasHistoryAsync(cancellationToken)) return NoHistory;

        var located = await LocateAsync(index, path, cancellationToken);
        if (located.Problem is not null) return located.Problem;
        if (located.File!.SkipReason is { } reason)
            return $"'{located.Spelled}' was not indexed ({reason}), so it has no lines to attribute.";

        int first = Math.Max(1, startLine);
        int last = endLine is null or < 1 ? int.MaxValue : endLine.Value;
        var runs = await index.BlameAsync(located.File.FileId, first, last, cancellationToken);
        if (runs.Count == 0)
            return $"'{located.Spelled}' has no lines between {first} and "
                   + (last == int.MaxValue ? "the end of the file." : $"{last}.");

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{located.Spelled}, who last changed each line:\n\n");
        foreach (var run in runs.Take(MaxBlameRuns))
        {
            string lines = run.StartLine == run.EndLine
                ? run.StartLine.ToString(CultureInfo.InvariantCulture)
                : $"{run.StartLine}-{run.EndLine}";
            // Built as a string first: a conditional expression is a string, not an interpolated-string
            // handler, so passing it to Append(IFormatProvider, ...) binds the char* overload instead.
            string entry = run.By is { } by
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{lines,-12} {by.Sha[..8]}  {by.AuthoredAt:yyyy-MM-dd}  {by.AuthorName} — {ToolReply.Clip(by.Subject)}\n")
                : string.Create(CultureInfo.InvariantCulture, $"{lines,-12} not attributed\n");
            text.Append(entry);
        }

        if (runs.Count > MaxBlameRuns)
            text.Append(CultureInfo.InvariantCulture,
                $"\n{runs.Count - MaxBlameRuns} further {ToolReply.Plural(runs.Count - MaxBlameRuns, "run")} not shown; narrow with startLine and endLine.\n");
        return ToolReply.Cap(text.ToString(), "Narrow with startLine and endLine.");
    }

    /// <summary>
    ///     A screenful. A churn ranking is read from the top down and the tail of one is noise: the
    ///     twentieth busiest file of a quarter is rarely what anybody was looking for.
    /// </summary>
    private const int DefaultHotFiles = 20;

    /// <summary>
    ///     A hundred files is already more than anybody reads off a ranking, and the reply cap would
    ///     bite around there anyway. High enough that an agent wanting the whole picture of a small
    ///     project gets it in one call.
    /// </summary>
    private const int MaxHotFiles = 100;

    [McpServerTool(Name = "hot_files", ReadOnly = true, Idempotent = true, Title = "Rank files by how much they changed")]
    [Description("""
                 Ranks the files that changed most over a window of history, most commits first, with the lines each gained and lost. Use it to find where a project is actually moving before reading any of it, and to tell a file that is edited constantly from one nobody has touched in a year.

                 - The window ends at the newest commit in the index, not at today: an index is built by a refresh and may be behind its remotes. The reply says which dates it covered, so a stale index shows as one.
                 - Scope it with `directory`, a qualified path: `main/src/Api` for one area, or a bare repository slug for one repository.
                 - Ranking is by number of commits, then by lines changed. A reformat counts as a change, the same way blame does — this is where work happened, not where the logic changed.
                 - Files a later commit deleted or renamed away are ranked too and marked; there is nothing at those paths to read now.
                 """)]
    public async Task<string> HotFiles(
        [Description("Days back from the newest recorded commit, 1-3650. Default 90.")]
        int days = HistoryWindow.DefaultDays,
        [Description(
            "Qualified path of a directory to rank within, e.g. \"main/src/Api\", or a repository slug alone for one repository. Default: the whole project.")]
        string? directory = null,
        [Description("Files to return, 1-100. Default 20.")]
        int limit = DefaultHotFiles,
        CancellationToken cancellationToken = default)
    {
        var open = await IndexReader.OpenAsync(indexes, BoundProject.Get(httpContextAccessor).Slug, null,
            cancellationToken);
        if (open is IndexOpen.Refused refused) return refused.Explanation;
        using var index = ((IndexOpen.Opened)open).Reader;
        if (!await index.HasHistoryAsync(cancellationToken)) return NoHistory;

        var scope = await ScopeAsync(index, directory, cancellationToken);
        if (scope.Problem is not null) return scope.Problem;

        // Which repositories the ranking can speak for, decided before the answer branches: an empty
        // ranking needs it as much as a full one does, and more — a reader shown nothing is the one
        // most likely to conclude that nothing changed.
        string? coverage = await HistoryCoverageAsync(index, scope.RepositorySlug, cancellationToken);

        var window = await index.WindowAsync(days, scope.RepositorySlug, cancellationToken);
        // The project has history and this scope has none: a repository whose walk found nothing, which
        // reads as "nobody has changed it" unless it is said outright.
        if (window is null)
            return $"No commit is recorded for {scope.Spelled}, although project '{index.ProjectSlug}' has history "
                   + "for other repositories. Its history could not be walked; git_log without a scope shows what was imported.";

        var ranked = await index.ChurnAsync(window, scope.RepositorySlug, scope.DirectoryInRepository,
            Math.Clamp(limit, 1, MaxHotFiles), cancellationToken);
        if (ranked.Count == 0)
            return $"No commit changed a file in {scope.Spelled} between {window.Describe()}. "
                   + $"The newest recorded commit there is {window.Until:yyyy-MM-dd}; raise days to look further back."
                   + (coverage is null ? "" : " " + coverage);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{ranked.Count} most-changed {ToolReply.Plural(ranked.Count, "file")} in {scope.Spelled}, {window.Describe()}:\n\n");

        foreach (var file in ranked)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{file.Commits,4} {ToolReply.Plural(file.Commits, "commit"),-8} +{file.Added,-7:N0} -{file.Deleted,-7:N0} {file.QualifiedPath}");
            if (!file.AtHead) text.Append("  (no longer at HEAD)");
            text.Append('\n');
        }

        if (coverage is not null) text.Append(CultureInfo.InvariantCulture, $"\n{coverage}\n");
        return ToolReply.Cap(text.ToString(), "Lower limit, or narrow with directory.");
    }

    /// <summary>
    ///     Which repositories of the project have imported history and which have none, or null when
    ///     the question does not arise — one repository, or a call already scoped to one. A ranking
    ///     that silently omits a repository whose walk failed is a ranking an agent reads as a complete
    ///     picture of the project (CONTEXT.md, History).
    /// </summary>
    private static async Task<string?> HistoryCoverageAsync(IndexReader index, string? scopedTo,
        CancellationToken cancellationToken)
    {
        if (scopedTo is not null) return null;
        var counts = await index.CommitCountsAsync(cancellationToken);
        if (counts.Count < 2) return null;

        var without = counts.Where(r => r.Commits == 0).Select(r => r.Slug).ToList();
        if (without.Count == 0) return null;

        var with = counts.Where(r => r.Commits > 0).Select(r => r.Slug).ToList();
        return $"History was imported for {string.Join(", ", with)} and for none of {string.Join(", ", without)}, "
               + "so nothing from those can appear above however much they changed.";
    }

    /// <summary>
    ///     What a <c>directory</c> argument narrows to: a repository, a directory inside it, or neither
    ///     for the whole project — with the sentence to answer with instead when it names a repository
    ///     the project does not have. <c>Spelled</c> is what the reply calls the scope, so the ranking
    ///     and the misses name it the same way.
    /// </summary>
    private static async Task<ChurnScope> ScopeAsync(IndexReader index, string? directory,
        CancellationToken cancellationToken)
    {
        string project = $"project '{index.ProjectSlug}'";
        if (string.IsNullOrWhiteSpace(directory)) return new ChurnScope(null, null, null, project);

        var paths = await index.PathsAsync(cancellationToken);
        // Null is the repository level, which only a multi-repository project has and which a non-empty
        // argument cannot parse to; a blank one was answered above.
        var qualified = paths.Parse(directory);
        if (qualified is null)
            return new ChurnScope(
                $"'{directory}' names no directory: {await index.PathRuleAsync(cancellationToken)}", null, null, "");

        var repository = await index.FindRepositoryAsync(qualified.RepositorySlug, cancellationToken);
        if (repository is null)
            return new ChurnScope(
                $"{await index.UnknownRepositoryAsync(qualified.RepositorySlug, cancellationToken)} The first path segment must be one of these.",
                null, null, "");

        string spelled = paths.Format(repository.Slug, qualified.PathInRepository);
        return new ChurnScope(null, repository.Slug,
            qualified.PathInRepository.Length == 0 ? null : qualified.PathInRepository,
            qualified.PathInRepository.Length == 0
                ? $"repository '{repository.Slug}' of {project}"
                : $"'{spelled}' in {project}");
    }

    /// <summary>What a ranking covers, or the sentence to answer with instead. Never both.</summary>
    private sealed record ChurnScope(
        string? Problem,
        string? RepositorySlug,
        string? DirectoryInRepository,
        string Spelled);

    private static void Append(StringBuilder text, RecordedChange commit, bool withRepository)
    {
        text.Append(CultureInfo.InvariantCulture,
            $"{commit.Sha[..8]}  {commit.AuthoredAt:yyyy-MM-dd}  {commit.AuthorName} <{commit.AuthorEmail}>");
        // The repository is named only when the answer spans several, so a single-repository project
        // and a scoped call do not repeat one slug down the whole reply.
        if (withRepository) text.Append(CultureInfo.InvariantCulture, $"  [{commit.RepositorySlug}]");
        text.Append(CultureInfo.InvariantCulture, $"\n            {ToolReply.Clip(commit.Subject)}\n");
    }

    private static string Scope(IndexReader index) =>
        index.Repository is { } repository ? $" in repository '{repository.Slug}'" : "";

    /// <summary>
    ///     Resolves a qualified path to the file the index holds, with the same misses spelled the same
    ///     way <c>read_file</c> spells them: a path that names no file and a path in an unknown
    ///     repository are answers an agent acts on, not errors.
    /// </summary>
    private static async Task<LocatedFile> LocateAsync(IndexReader index, string path,
        CancellationToken cancellationToken)
    {
        var paths = await index.PathsAsync(cancellationToken);
        var qualified = paths.Parse(path);
        if (qualified is null || qualified.PathInRepository.Length == 0)
            return new LocatedFile(
                $"'{path}' names no file: {await index.PathRuleAsync(cancellationToken)} Write it like `{paths.Example()}`.");

        var repository = await index.FindRepositoryAsync(qualified.RepositorySlug, cancellationToken);
        if (repository is null)
            return new LocatedFile(
                $"{await index.UnknownRepositoryAsync(qualified.RepositorySlug, cancellationToken)} The first path segment must be one of these.");

        qualified = qualified with { RepositorySlug = repository.Slug };
        string spelled = paths.Format(qualified);
        var file = await index.FindFileAsync(spelled, cancellationToken);
        return file is null
            ? new LocatedFile($"No indexed file '{spelled}' in project '{index.ProjectSlug}'. "
                              + "Use glob or list_tree to locate it.")
            : new LocatedFile(null, file, repository.Slug, qualified.PathInRepository, spelled);
    }

    /// <summary>A resolved file, or the sentence to answer with instead. Never both.</summary>
    private sealed record LocatedFile(
        string? Problem,
        IndexedFile? File = null,
        string? RepositorySlug = null,
        string? PathInRepository = null,
        string Spelled = "");
}
