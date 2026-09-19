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
internal sealed class HistoryTools(IHttpContextAccessor httpContextAccessor, HistoryQueries history)
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

    private string Project => BoundProject.Get(httpContextAccessor).Slug;

    [McpServerTool(Name = "git_log", ReadOnly = true, Idempotent = true, Title = "List the project's commits")]
    [Description("""
                 Lists commits of the project's default branch, newest first. Use it to see what changed recently, and how much of a project is moving, before asking about any one file.

                 - `repo` scopes it to one repository, `author` to one person; `limit` and `page` walk it. Those are its only arguments.
                 - `author` matches the email address, not the display name: `grace@example.com` or `grace`, never "Grace Hopper". `authors` lists the addresses.
                 - Nothing else filters — not by message, by one commit or by date. Any other argument name, `grep` and `commit` included, is named as ignored above the answer.
                 - For what it cannot answer: page back for older commits, authors for who has worked here, file_history for one file, blame for one line, hot_files for where the work is.
                 - Merges count as one commit and their side branches are not walked, so a pull request reads as a single change.
                 - Only the default branch is recorded. A commit on a branch that was never merged is not here.
                 - History may not reach the beginning of the repository, and it is not the same as the code.
                 """)]
    public async Task<string> GitLog(
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        [Description(
            "Email address, whole or in part, e.g. \"grace@example.com\" or \"grace\". Matches the address and not the display name, case-insensitively. Default: every author.")]
        string? author = null,
        [Description("Commits to return, 1-200. Default 30.")]
        int limit = DefaultCommits,
        [Description("1-based page of results, newest first.")]
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        // A name this tool does not have binds nowhere — every parameter is optional — and the call
        // still runs with its defaults, which is an unfiltered log answering a filtered question:
        // well-formed, plausible and wrong (#86). What was sent and ignored is said above this answer
        // by the call-tool filter (ToolArguments), which reads the caller's raw arguments and so
        // catches every spelling rather than the four this tool once declared to catch them.
        var outcome = await history.LogAsync(Project, new LogRequest(repo, limit, page, author), cancellationToken);
        return ToolReply.Render<LogAnswer>(outcome, Log,
            answer => $"Narrow with repo or author, or raise page past {answer.Page}.");
    }

    private static string Log(LogAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // Before the empty-page reading, because an address that matches nobody and a page past the
        // end of a real author's commits are opposite facts and the second sentence would fit both.
        if (answer.Author is { Addresses: 0 } miss) return NoSuchAuthor(miss, answer.Repository);

        int skip = (answer.Page - 1) * answer.Limit;
        if (answer.Commits.Count == 0)
            return skip > 0
                ? $"No commits on page {answer.Page}. There are fewer than {skip + 1} commits{By(answer.Author)} recorded{Scope(answer.Repository)}."
                : $"No commits{By(answer.Author)} are recorded{Scope(answer.Repository)}.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Commits.Count} {ToolReply.Plural(answer.Commits.Count, "commit")}{By(answer.Author)}{Scope(answer.Repository)}, newest first:\n\n");
        foreach (var commit in answer.Commits) Append(text, commit, answer.Repository is null);
        if (answer.Author is { } filter) Matched(text, filter, answer.Commits.Count);
        return text.ToString();
    }

    /// <summary>
    ///     A filtered miss, which must not read as a clean negative: "no commits by Holger" and "no
    ///     address here contains Holger" mean opposite things to an agent, and the first is what an
    ///     unqualified empty answer says (CODING_STANDARDS, Errors). The count is what makes it
    ///     actionable — a project with authors and no match is a misspelling — and the rule it was
    ///     probably broken against is the one the name-shaped guess breaks.
    /// </summary>
    private static string NoSuchAuthor(AuthorFilter filter, IndexedRepository? repository) =>
        string.Create(CultureInfo.InvariantCulture,
            $"No address contains '{filter.Query}'{Scope(repository)} — `author` matches the address, not the name. {filter.AuthorsInScope} {ToolReply.Plural(filter.AuthorsInScope, "address", "addresses")} recorded; call authors to list them.");

    /// <summary>
    ///     Who the filter matched, under the log it narrowed. Said where the header cannot already have
    ///     said it: a substring that caught two addresses, or a total the page does not reach. One
    ///     address whose commits all fit is what the header states, and repeating it there is what
    ///     teaches an agent to skim past the line that matters.
    /// </summary>
    private static void Matched(StringBuilder text, AuthorFilter filter, int shown)
    {
        if (filter.Addresses == 1 && filter.Commits == shown) return;

        // The addresses listed are capped, so the count leads and the rows follow it: a broad filter
        // says how wide it was even where naming every address it caught would be the whole reply.
        text.Append(string.Create(CultureInfo.InvariantCulture,
            $"\n'{filter.Query}' matched {filter.Addresses} {ToolReply.Plural(filter.Addresses, "address", "addresses")}, {filter.Commits} {ToolReply.Plural(filter.Commits, "commit")} in all"));
        text.Append(filter.Addresses > filter.Matched.Count
            ? string.Create(CultureInfo.InvariantCulture, $" ({filter.Matched.Count} shown):\n")
            : ":\n");
        foreach (var one in filter.Matched) ToolReply.AuthorRow(text, "  ", one);
    }

    /// <summary>What the count in a sentence is counting, when a filter narrowed it.</summary>
    private static string By(AuthorFilter? filter) => filter is null ? "" : $" by an address matching '{filter.Query}'";

    [McpServerTool(Name = "authors", ReadOnly = true, Idempotent = true, Title = "List who has committed")]
    [Description("""
                 Lists who has committed, most commits first, with the address each one commits from. Read it before filtering git_log by `author`, which matches that address.

                 - `repo` scopes it to one repository; `limit` cuts the list.
                 - One row is one address: two addresses are two rows, and a respelled name is one row under the newest spelling. Git records the address as the identity.
                 - Counts are commits over the whole imported history, not lines and not a recent window. hot_files is what is moving now; this is who has been here.
                 - It says who touched the code, never who wrote it: a reformat is a commit, so a mass change makes its author look expert in files they only reindented.
                 """)]
    public async Task<string> Authors(
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        [Description("Authors to return, 1-200. Default 30.")]
        int limit = DefaultAuthors,
        CancellationToken cancellationToken = default)
    {
        return ToolReply.Render<AuthorsAnswer>(
            await history.AuthorsAsync(Project, new AuthorsRequest(repo, limit), cancellationToken),
            AuthorList, "Lower limit to see fewer, or scope with repo.");
    }

    private static string AuthorList(AuthorsAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // A project with history and no author is not reachable — a commit carries one — so this is
        // about a repository scope that holds no commits, and says that rather than "nobody".
        if (answer.Authors.Count == 0) return $"No commits are recorded{Scope(answer.Repository)}, so no authors are.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Total} {ToolReply.Plural(answer.Total, "author")}{Scope(answer.Repository)}, most commits first");
        // Only where the list was cut, so the common answer does not carry arithmetic nobody needs.
        text.Append(answer.Total > answer.Authors.Count
            ? string.Create(CultureInfo.InvariantCulture, $" ({answer.Authors.Count} shown, limit {answer.Limit}):\n\n")
            : ":\n\n");
        foreach (var author in answer.Authors) ToolReply.AuthorRow(text, "", author);
        return text.ToString();
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
        return ToolReply.Render<FileHistoryAnswer>(
            await history.FileHistoryAsync(Project, new FileHistoryRequest(path, limit), cancellationToken),
            Changes, "Lower limit to see fewer.");
    }

    private static string Changes(FileHistoryAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;

        string spelled = answer.File.QualifiedPath;
        if (answer.Commits.Count == 0)
            return $"No commit in the recorded history changed '{spelled}'. "
                   + "The file is in the index, so this means its history is older than what was imported, or it "
                   + "reached this path by a rename — try blame, which follows the content rather than the path.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Commits.Count} {ToolReply.Plural(answer.Commits.Count, "commit")} changed {spelled}, newest first:\n\n");
        foreach (var commit in answer.Commits) Append(text, commit, false);
        return text.ToString();
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
        return ToolReply.Render<BlameAnswer>(
            await history.BlameAsync(Project, new BlameRequest(path, startLine, endLine), cancellationToken),
            Attribution, "Narrow with startLine and endLine.");
    }

    private static string Attribution(BlameAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;

        string spelled = answer.File.QualifiedPath;
        if (answer.File.SkipReason is { } reason)
            return $"'{spelled}' was not indexed ({reason}), so it has no lines to attribute.";
        if (answer.Runs.Count == 0)
            return $"'{spelled}' has no lines between {answer.First} and "
                   + (answer.Last is null ? "the end of the file." : $"{answer.Last}.");

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{spelled}, who last changed each line:\n\n");
        foreach (var run in answer.Runs.Take(MaxBlameRuns))
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

        if (answer.Runs.Count > MaxBlameRuns)
            text.Append(CultureInfo.InvariantCulture,
                $"\n{answer.Runs.Count - MaxBlameRuns} further {ToolReply.Plural(answer.Runs.Count - MaxBlameRuns, "run")} not shown; narrow with startLine and endLine.\n");
        return text.ToString();
    }

    /// <summary>
    ///     A screenful. Both rankings here are read from the top down and the tail of one is noise: the
    ///     twentieth busiest file of a quarter, and the twentieth file a path shares one commit with,
    ///     are equally rarely what anybody was looking for. One number because the two tools also tell
    ///     an agent the same range in their own prose, and two would eventually disagree.
    /// </summary>
    private const int DefaultRankedFiles = 20;

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
        int limit = DefaultRankedFiles,
        CancellationToken cancellationToken = default)
    {
        string project = Project;
        return ToolReply.Render<ChurnAnswer>(
            await history.ChurnAsync(project, new ChurnRequest(directory, days, limit), cancellationToken),
            answer => Ranking(answer, project), "Lower limit, or narrow with directory.");
    }

    private static string Ranking(ChurnAnswer answer, string projectSlug)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // The project has history and this scope has none: a repository whose walk found nothing, which
        // reads as "nobody has changed it" unless it is said outright. This answer names the scope it
        // is about, so the coverage caveat below would only repeat it — and is not paid for here.
        if (answer.Window is not { } window) return NoCommitsIn(answer.ScopeSpelled, projectSlug);

        // Said whether or not there is a ranking, and more when there is not: a reader shown nothing is
        // the one most likely to conclude that nothing changed.
        string? coverage = Coverage(answer.Coverage);

        if (answer.Files.Count == 0)
            return $"No commit changed a file in {answer.ScopeSpelled} between {window.Describe()}. "
                   + $"The newest recorded commit there is {window.Until:yyyy-MM-dd}; raise days to look further back."
                   + (coverage is null ? "" : " " + coverage);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Files.Count} most-changed {ToolReply.Plural(answer.Files.Count, "file")} in {answer.ScopeSpelled}, {window.Describe()}:\n\n");

        foreach (var file in answer.Files) ToolReply.ChurnRow(text, "", file);

        if (coverage is not null) text.Append(CultureInfo.InvariantCulture, $"\n{coverage}\n");
        return text.ToString();
    }

    [McpServerTool(Name = "co_changed", ReadOnly = true, Idempotent = true,
        Title = "Find the files that usually change with a file")]
    [Description("""
                 Ranks the files that were committed alongside one file over a window of history, most shared commits first. Use it once you have found the file you need to change, to find what usually has to change with it: the coupling the code does not show — a constant and the places that read it, a stored procedure and its caller, two files that have simply always moved together.

                 - This is evidence and not proof. Two files in one reformat share a commit without sharing anything else, and the ranking says how many commits each pair shares so you can tell a habit from an accident.
                 - The window ends at the newest commit in the index, not at today, and the reply says which dates it covered.
                 - Commits that touched a great many paths at once are left out of the pairing: one reformat or vendor drop pairs every path it touched with every other and would swamp the answer. The reply says when that happened.
                 - Pairing never crosses a repository, because a commit does not.
                 - History is matched by path, so it begins where the file was last renamed.
                 """)]
    public async Task<string> CoChanged(
        [Description("Qualified path of one file, e.g. \"main/src/Api/Foo.cs\".")]
        string path,
        [Description("Days back from the newest recorded commit, 1-3650. Default 90.")]
        int days = HistoryWindow.DefaultDays,
        [Description("Files to return, 1-100. Default 20.")]
        int limit = DefaultRankedFiles,
        CancellationToken cancellationToken = default)
    {
        string project = Project;
        return ToolReply.Render<CoChangeAnswer>(
            await history.CoChangedAsync(project, new CoChangeRequest(path, days, limit), cancellationToken),
            answer => Coupling(answer, project), "Lower limit to see fewer.");
    }

    private static string Coupling(CoChangeAnswer answer, string projectSlug)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;

        string spelled = answer.File.QualifiedPath;
        if (answer.Window is not { } window) return NoCommitsIn($"repository '{answer.File.RepositorySlug}'", projectSlug);

        var coupling = answer.Coupling;
        // Said before the answer branches: a window that reached none of the file's commits is not a
        // file that moves alone, and an agent shown the wrong one of those two learns a wrong fact.
        if (coupling.Commits == 0)
            // The window names its own end, so the repository is not named here: in a single-repository
            // project its slug is one the operator never assigned and no path an agent holds contains.
            return $"No commit changed {spelled} between {window.Describe()}, so there is nothing it "
                   + "could have changed alongside; raise days to look further back.";

        if (coupling.Files.Count == 0)
            return $"No other file was changed by any of the {coupling.Paired} "
                   + $"{ToolReply.Plural(coupling.Paired, "commit")} that touched {spelled} between "
                   + $"{window.Describe()}. It moves alone in the history that was imported, which is evidence "
                   + $"and not proof: that history begins where the file was last renamed."
                   + (coupling.Excluded == 0 ? "" : " " + ExcludedNote(coupling, answer.MaxCommitPaths));

        string files = ToolReply.Plural(coupling.Files.Count, "file");
        string commits = ToolReply.Plural(coupling.Commits, "commit");
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            // Commits and not Paired: Paired is what the ceiling left to pair with, and calling that
            // "the commits that touched it" understates the file's history by exactly the number the
            // note below then quotes — one sentence contradicting the next, with the smaller number
            // leading (#116). The pairing's own denominator is said where it is explained.
            $"{coupling.Files.Count} {files} changed alongside {spelled}, out of the {coupling.Commits} {commits} that touched it, {window.Describe()}.\n");
        // Above the ranking and not below it: a full ranking is exactly where the reply cap bites, and
        // it is also exactly where knowing that a third of the file's commits were left out matters.
        if (coupling.Excluded > 0)
            text.Append(CultureInfo.InvariantCulture, $"{ExcludedNote(coupling, answer.MaxCommitPaths)}\n");
        text.Append('\n');

        foreach (var file in coupling.Files)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{file.SharedCommits,4} shared {ToolReply.Plural(file.SharedCommits, "commit"),-8} ");
            ToolReply.RankedPath(text, file.QualifiedPath, file.AtHead);
        }

        return text.ToString();
    }

    /// <summary>
    ///     What the ceiling kept out, in one sentence, for a caller that has already established there
    ///     was something. Said rather than dropped: a ranking drawn from a third of a file's commits
    ///     without saying so is the one that misleads, and the number is also how a caller learns the
    ///     ceiling is wrong for their repository.
    ///     The sentence carries no leading separator, because the two callers want different ones.
    /// </summary>
    private static string ExcludedNote(CoChanges coupling, int maxCommitPaths)
    {
        // Built in one piece and only then concatenated: an interpolated string joined to another with
        // `+` is a string, not a handler, and the culture-aware overloads bind to char* instead.
        string counted = string.Create(CultureInfo.InvariantCulture,
            $"{coupling.Excluded} of its {coupling.Commits} commits touched more than {maxCommitPaths} paths");
        return counted
               + $" and {ToolReply.Plural(coupling.Excluded, "was", "were")} left out of the pairing: a commit "
               + "that size pairs every path it touched with every other, which is one commit and not coupling. "
               + "An operator can raise History:MaxCommitPaths where a repository really does land changes that wide.";
    }

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
}
