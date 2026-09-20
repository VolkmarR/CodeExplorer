using System.ComponentModel;
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

                 - `repo` scopes it to one repository, `path` to a folder or a file inside one, `author` to one person, `message` to what a commit says it did; `limit` and `page` walk it. Those are its only arguments, and they combine.
                 - `path` is a qualified path, exactly as grep, glob and list_tree print one: `main/src/Api` for everything under a folder, or `main/src/Api/Orders.cs` for one file. It is what makes "what has been happening in this folder" one call. Scoping is by the path each commit recorded, so it begins where a file was last renamed. A path HEAD no longer holds is still scopeable: a file a later commit deleted, or a folder it renamed away, has commits to list and nothing to open, and the answer says so.
                 - `author` matches the email address, not the display name: `grace@example.com` or `grace`, never "Grace Hopper". `authors` lists the addresses.
                 - `message` matches text in the subject line, case-insensitively: a ticket key, a PR number, a release name. A ticket or PR number lives in the commit message and almost never in the code, so look for it here rather than with grep. It searches the subject only, not the body, and it is text and not a pattern — `%` and `_` match themselves.
                 - Nothing else filters — not by one commit and not by date. Any other argument name is named as ignored above the answer.
                 - It is not project-wide only: `repo` and `path` narrow it, and `file_history` is the same read for one exact path.
                 - For what it cannot answer: page back for older commits, authors for who has worked here, file_history for one file, blame for one line, hot_files for where the work is, commit and commit_files for the message and the paths of one commit found here.
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
        [Description(
            "Text in the commit's subject line, e.g. \"BugFix 558185\", \"PR 39371\" or \"release\". Matched case-insensitively as text, not as a pattern, and against the subject only. Default: every commit.")]
        string? message = null,
        [Description("Commits to return, 1-200. Default 30.")]
        int limit = DefaultCommits,
        [Description("1-based page of results, newest first.")]
        int page = 1,
        [Description(
            "Qualified path of a folder or a file to scope to, e.g. \"main/src/Api\" or \"main/src/Api/Orders.cs\". Matched by the path each commit recorded, so it begins where a file was last renamed. A path HEAD no longer holds still scopes, because history recorded it. Default: the whole project.")]
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        // A name this tool does not have binds nowhere — every parameter is optional — and the call
        // still runs with its defaults, which is an unfiltered log answering a filtered question:
        // well-formed, plausible and wrong (#86). What was sent and ignored is said above this answer
        // by the call-tool filter (ToolArguments), which reads the caller's raw arguments and so
        // catches every spelling rather than the four this tool once declared to catch them.
        var outcome = await history.LogAsync(Project, new LogRequest(repo, limit, page, author, message, path),
            cancellationToken);
        return ToolReply.Render<LogAnswer>(outcome, Log,
            answer => $"Narrow with repo, path or author, or raise page past {answer.Page}.");
    }

    private static string Log(LogAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // Before the empty-page reading, because an address that matches nobody and a page past the
        // end of a real author's commits are opposite facts and the second sentence would fit both.
        if (answer.Author is { Addresses: 0 } miss) return NoSuchAuthor(miss, answer.Repository);

        int skip = (answer.Page - 1) * answer.Limit;
        string where = Scope(answer.Repository, answer.Path);
        if (answer.Commits.Count == 0)
            return (skip > 0
                       ? $"No commits on page {answer.Page}. There are fewer than {skip + 1} commits{By(answer.Author)}{Saying(answer.Message)} recorded{where}."
                       // A subject filter that matched nothing is named as the filter it is, and points
                       // at the half of the search this tool does not do: the message is all it reads,
                       // so a number that only ever appears in the code is a miss here and a grep
                       // somewhere else.
                       : answer.Message is { } missed
                           ? $"No commit's subject contains '{missed}'{By(answer.Author)}{where}. "
                             + "The subject is the only text searched — not the message body, and not the code. "
                             + "grep searches the code."
                           : $"No commits{By(answer.Author)} are recorded{where}.")
                   + ByRecordedPath(answer.Path);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Commits.Count} {ToolReply.Plural(answer.Commits.Count, "commit")}{By(answer.Author)}{Saying(answer.Message)}{where}, newest first:\n\n");
        foreach (var commit in answer.Commits) Append(text, commit, answer.Repository is null && answer.Path is null);
        if (answer.Author is { } filter) Matched(text, filter, answer.Commits.Count);
        AppendRecordedPathNote(text, answer.Path);
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

    /// <summary>
    ///     The other half of that, for the subject filter. Written the same way and appended in the
    ///     same places, so a log narrowed by both says so twice rather than once — a header that
    ///     mentioned one filter and not the other would be the #86 fault with a smaller blast radius.
    /// </summary>
    private static string Saying(string? message) =>
        message is null ? "" : $" whose subject contains '{message}'";

    [McpServerTool(Name = "authors", ReadOnly = true, Idempotent = true, Title = "List who has committed")]
    [Description("""
                 Lists who has committed, most commits first, with the address each one commits from. Read it before filtering git_log by `author`, which matches that address.

                 - `repo` scopes it to one repository and `path` to a folder or a file inside one, which is what makes "who owns this folder" one call rather than a file_history per file; `limit` cuts the list.
                 - `path` is a qualified path, exactly as grep, glob and list_tree print one: `main/src/Api`, or `main/src/Api/Orders.cs` for one file. Scoping is by the path each commit recorded, so it begins where a file was last renamed — a folder that was moved reads as a quiet one unless you know that. A path HEAD no longer holds is still scopeable: a deleted or renamed-away path has authors to list and nothing to open, and the answer says so.
                 - One row is one address: two addresses are two rows, and a respelled name is one row under the newest spelling. Git records the address as the identity.
                 - Counts are commits over the whole imported history, not lines and not a recent window. hot_files is what is moving now; this is who has been here.
                 - It says who touched the code, never who wrote it: a reformat is a commit, so a mass change makes its author look expert in files they only reindented.
                 """)]
    public async Task<string> Authors(
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        [Description("Authors to return, 1-200. Default 30.")]
        int limit = DefaultAuthors,
        [Description(
            "Qualified path of a folder or a file to scope to, e.g. \"main/src/Api\" or \"main/src/Api/Orders.cs\". Matched by the path each commit recorded, so it begins where a file was last renamed. A path HEAD no longer holds still scopes, because history recorded it. Default: the whole project.")]
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        return ToolReply.Render<AuthorsAnswer>(
            await history.AuthorsAsync(Project, new AuthorsRequest(repo, limit, path), cancellationToken),
            AuthorList, "Lower limit to see fewer, or scope with repo or path.");
    }

    private static string AuthorList(AuthorsAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // A project with history and no author is not reachable — a commit carries one — so this is
        // about a repository scope that holds no commits, and says that rather than "nobody".
        string where = Scope(answer.Repository, answer.Path);
        if (answer.Authors.Count == 0)
            return $"No commits are recorded{where}, so no authors are."
                   + ByRecordedPath(answer.Path);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Total} {ToolReply.Plural(answer.Total, "author")}{where}, most commits first");
        // Only where the list was cut, so the common answer does not carry arithmetic nobody needs.
        text.Append(answer.Total > answer.Authors.Count
            ? string.Create(CultureInfo.InvariantCulture, $" ({answer.Authors.Count} shown, limit {answer.Limit}):\n\n")
            : ":\n\n");
        foreach (var author in answer.Authors) ToolReply.AuthorRow(text, "", author);
        AppendRecordedPathNote(text, answer.Path);
        return text.ToString();
    }

    [McpServerTool(Name = "file_history", ReadOnly = true, Idempotent = true,
        Title = "List the commits that changed a file")]
    [Description("""
                 Lists the commits that changed one file, newest first. This is the tool for "who has worked on this" and "when was this last touched".

                 - The path is qualified, exactly as grep and read_file print it: `main/src/Api/Foo.cs`.
                 - History is matched by path, so it begins where the file was last renamed or moved. An empty or short answer on an old file usually means a move, not that nobody touched it — the content's line-by-line history survives a move and is what blame reports.
                 - It says who changed the file and when, never what they changed: the diffs are not indexed. commit_files takes a SHA listed here and names every other path that commit touched.
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

    [McpServerTool(Name = "commit", ReadOnly = true, Idempotent = true, Title = "Read one commit's record")]
    [Description("""
                 Reports one commit: who made it and when, its whole message — subject and body — and how much it changed. Reach for it with a SHA another history tool already printed, when the one-line subject is not enough.

                 - `sha` takes as much of the SHA as you have. The eight characters git_log, file_history and blame print are enough; a prefix that fits more than one commit is refused rather than guessed at.
                 - The message body is here and nowhere else: git_log lists subjects alone, so whatever a subject like "BugFix 558185 - Fehlermeldung" does not say is read here.
                 - It says how many files the commit touched and how many lines it added and removed. commit_files names the paths.
                 - It cannot show the diff. No hunks, no before-and-after, no changed line: the index records which paths a commit touched and how many lines, never the change itself. Read the file at HEAD instead.
                 - It cannot find a commit for you. Nothing searches commit messages — not this tool and not git_log — so a ticket number is found by paging git_log or by grepping the code, and only then asked about here.
                 - Only the default branch is recorded. A commit on a branch that was never merged is not here.
                 """)]
    public async Task<string> Commit(
        [Description("The commit's SHA, whole or its first characters, e.g. \"a1b2c3d4\".")]
        string sha,
        CancellationToken cancellationToken = default)
    {
        return ToolReply.Render<CommitAnswer>(
            await history.CommitAsync(Project, new CommitRequest(sha), cancellationToken), Record,
            "That length is the commit's own message; commit_files lists what it touched separately.");
    }

    private static string Record(CommitAnswer answer)
    {
        var commit = answer.Commit;
        var text = new StringBuilder();
        // The whole SHA, not the eight characters the listings print: a caller that arrived with a
        // prefix leaves with the one spelling every other surface — the API, a link, git itself —
        // agrees on.
        text.Append(CultureInfo.InvariantCulture,
            $"{commit.Sha}  [{commit.RepositorySlug}]\n{commit.AuthorName} <{commit.AuthorEmail}>, {commit.AuthoredAt:yyyy-MM-dd}\n\n");
        text.Append(CultureInfo.InvariantCulture, $"{ToolReply.Clip(commit.Subject)}\n");
        // Said rather than left as a blank: an answer that simply stops after the subject reads as a
        // body that was cut, and an agent that believes there is more goes looking for the tool to
        // read it with.
        text.Append(commit.Body.Length == 0 ? "\n(no message body)\n" : $"\n{commit.Body}\n");
        // Built in one piece before it is joined: an interpolated string concatenated with `+` is a
        // string and not a handler, and the culture-aware overload binds to char* instead.
        string sums = string.Create(CultureInfo.InvariantCulture,
            $"\n{commit.FilesChanged} {ToolReply.Plural(commit.FilesChanged, "file")} changed, +{commit.Added:N0} -{commit.Deleted:N0}. ");
        text.Append(sums).Append("Call commit_files for the paths; the diff itself is not indexed.\n");
        return text.ToString();
    }

    [McpServerTool(Name = "commit_files", ReadOnly = true, Idempotent = true,
        Title = "List the paths one commit touched")]
    [Description("""
                 Lists every path one commit touched, with what it did to each and the lines each gained and lost. This is the tool for "what shipped under this change": one call, rather than guessing which files a commit is likely to have touched and calling file_history on each of them until one matches.

                 - `sha` takes as much of the SHA as you have, like commit: the eight characters the other history tools print are enough.
                 - A page holds at most 300 paths, and the header always counts the whole commit. A mass rename or a version bump across a repository runs past that, so the reply says how many it listed and the `offset` that asks for the rest — where an incomplete list read as the whole commit is exactly the wrong answer to "is this everything that shipped?".
                 - Each path carries git's own word for what happened to it — added, modified, deleted, renamed.
                 - A path HEAD still holds is named the way grep and read_file name it, so it can be opened directly. A path this commit deleted, or a later one renamed away, is named too and marked `(no longer at HEAD)`; there is nothing at it to read now.
                 - It cannot show the diff: which paths, and how many lines, is all the index holds of a change. Read the file at HEAD to see what it says today.
                 - It cannot find the commit for you. Nothing here searches commit messages — not this tool and not git_log — so a ticket or PR number in a subject is found by paging git_log, never by grepping the code, and only then asked about here.
                 - Call commit for the message, the author and the totals.
                 - Only the default branch is recorded. A commit on a branch that was never merged is not here.
                 """)]
    public async Task<string> CommitFiles(
        [Description("The commit's SHA, whole or its first characters, e.g. \"a1b2c3d4\".")]
        string sha,
        [Description(
            "Skip this many paths, in path order, and list the page after them. Default 0. Pass the offset the previous reply named to continue where it stopped.")]
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return ToolReply.Render<CommitFilesAnswer>(
            await history.CommitFilesAsync(Project, new CommitFilesRequest(sha), cancellationToken),
            answer => Touched(answer, Math.Max(0, offset)),
            "Nothing narrows this list, and a page already holds at most "
            + $"{MaxPathsListed.ToString(CultureInfo.InvariantCulture)} paths; call commit for the file count "
            + "and the line sums without the list.");
    }

    /// <summary>
    ///     The most paths one <c>commit_files</c> reply lists (#125). A mass rename or a version bump
    ///     across a repository touches hundreds of paths, and the list was being cut by the reply
    ///     ceiling itself, which says nothing about what it cut — an agent asking "is this everything
    ///     that shipped?" read half a commit as the whole of it.
    ///     The page is taken here and not in <see cref="HistoryQueries" />: it is the reply that cannot
    ///     hold the list, and the commit page draws every path a commit touched beside a count of them
    ///     that has to agree with it. Reading all of one commit's rows to print some of them is one
    ///     commit's worth of work either way.
    /// </summary>
    internal const int MaxPathsListed = 300;

    /// <summary>
    ///     What the rows of one page may spend, under <see cref="ToolReply.MaxOutputChars" /> by the
    ///     room the header, the note and the closing sentence need. A count alone is not enough: a row
    ///     is a path, and three hundred of the long ones a mass rename moves — nested, marked
    ///     <c>(no longer at HEAD)</c> — overrun the reply ceiling, which then cuts the tail of the
    ///     page while the note still names the offset after it. Those rows would be unreachable at any
    ///     offset, which is the silent truncation this whole change is about, arrived at from the
    ///     inside. So the page ends at whichever comes first, and the offset it names is the one it
    ///     actually reached.
    /// </summary>
    private const int MaxPathChars = 36 * 1024;

    private static string Touched(CommitFilesAnswer answer, int offset)
    {
        int total = answer.Files.Count;
        // Paging past the last path is the end of the list, and must not read as the empty commit
        // CommitFilesAsync refuses outright: those are opposite facts about a commit the caller holds.
        if (offset >= total)
            return string.Create(CultureInfo.InvariantCulture,
                $"{answer.Sha} touched {total} {ToolReply.Plural(total, "path")} and has no path past the first {offset}: that was the end of the list. Call commit_files without an offset for the first page.\n");

        // The rows before the header, because how many of them fit is what the header has to say. One
        // row is always taken, however long its path: a page of nothing would page forever.
        var rows = new List<string>();
        int spent = 0;
        foreach (var file in answer.Files.Skip(offset).Take(MaxPathsListed))
        {
            var row = new StringBuilder();
            row.Append(CultureInfo.InvariantCulture,
                $"{file.ChangeKind,-9} +{file.Added,-7:N0} -{file.Deleted,-7:N0} ");
            // The same mark every ranking drawn from history uses, for the same reason: one surface
            // spelling it its own way is an agent sent to open a file that is not there.
            ToolReply.RankedPath(row, file.QualifiedPath, file.AtHead);
            if (rows.Count > 0 && spent + row.Length > MaxPathChars) break;
            spent += row.Length;
            rows.Add(row.ToString());
        }

        var text = new StringBuilder();
        // The commit's own count leads, whichever slice of it follows: a header counting the page is
        // the truncation this note exists to deny, said in the one line an agent is sure to read.
        text.Append(CultureInfo.InvariantCulture,
            $"{total} {ToolReply.Plural(total, "path")} changed by {answer.Sha}, in path order");
        int next = offset + rows.Count;
        if (offset == 0 && next == total) text.Append(":\n\n");
        else
        {
            text.Append(CultureInfo.InvariantCulture,
                $"; listing {rows.Count} of them, from number {offset + 1}.\n");
            if (next < total)
                text.Append(CultureInfo.InvariantCulture,
                    $"NOTE: {total - next} further {ToolReply.Plural(total - next, "path")} not shown. Call commit_files again with offset={next} for the next page.\n");
            text.Append('\n');
        }

        foreach (string row in rows) text.Append(row);

        text.Append("\nThe counts are lines, from this commit's own diff against its first parent; "
                    + "the diff itself is not indexed, so what changed in a line cannot be shown.\n");
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
                 - `depth` ranks **directories** instead of files. Reach for it when the question is which module, package or app is moving rather than which file — that is one call, where ranking files and then scoping the tool to each candidate directory in turn is one call per directory. Then call it again without `depth`, scoped to the directory that won, for the files inside it.
                 - IMPORTANT: a machine-authored commit counts exactly like a hand-written one. Regenerated output, a mechanical version bump across unrelated modules and a bulk rename all rank like real work, and a directory of generated files can outrank the code that generates it. `exclude` is the answer, in grep's syntax: `exclude="*.g.ts,*.generated.*,/migrations/,package-lock.json"`. Nothing is excluded by default and no naming convention is assumed — look at the top of an unfiltered ranking first, then exclude what the project turns out to regenerate. The reply says how many paths the filter hid.
                 """)]
    public async Task<string> HotFiles(
        [Description("Days back from the newest recorded commit, 1-3650. Default 90.")]
        int days = HistoryWindow.DefaultDays,
        [Description(
            "Qualified path of a directory to rank within, e.g. \"main/src/Api\", or a repository slug alone for one repository. Default: the whole project.")]
        string? directory = null,
        [Description("Rows to return — files, or directories under `depth` — 1-100. Default 20.")]
        int limit = DefaultRankedFiles,
        [Description(
            "Rank directories instead of files, grouped by this many path segments beneath the scope, 1-10. Unscoped, that is the first segments of the qualified path, so in a multi-repository project depth 1 ranks repositories and depth 2 their top-level directories. A directory's count is the commits that touched anything beneath it, each counted once. Default: rank files.")]
        int? depth = null,
        [Description(
            "Drop files whose qualified path matches any of these comma-separated terms, e.g. \"*.g.cs,/migrations/\". Same syntax as grep: a term with * or ? is a glob over the whole qualified path, anything else a plain substring, and matching is case-insensitive. Default: rank everything.")]
        string? exclude = null,
        CancellationToken cancellationToken = default)
    {
        string project = Project;
        return ToolReply.Render<ChurnAnswer>(
            await history.ChurnAsync(project, new ChurnRequest(directory, days, limit, depth, exclude),
                cancellationToken),
            answer => Ranking(answer, project),
            "Lower limit, narrow with directory, or roll the ranking up with depth.");
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

        // What the exclude kept out, said wherever there is an answer to misread: an excluded ranking
        // must never read as an unfiltered one, and an empty excluded ranking must never read as a
        // scope where nothing changed.
        string hidden = answer.Hidden == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $" exclude hid {answer.Hidden} {ToolReply.Plural(answer.Hidden, "path")}, so this is a filtered ranking.");

        if (answer.Files.Count == 0)
            return $"No commit changed a file in {answer.ScopeSpelled} between {window.Describe()}. "
                   + $"The newest recorded commit there is {window.Until:yyyy-MM-dd}; raise days to look further back."
                   + hidden
                   + (coverage is null ? "" : " " + coverage);

        string rows = answer.Depth is null
            ? ToolReply.Plural(answer.Files.Count, "file")
            : ToolReply.Plural(answer.Files.Count, "directory", "directories");
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Files.Count} most-changed {rows} in {answer.ScopeSpelled}, {window.Describe()}:\n");
        // Its own line above the ranking rather than a clause in the header: an unfiltered call must
        // read exactly as it did before, and the rows below this are the ones that survived.
        if (answer.Hidden > 0) text.Append(CultureInfo.InvariantCulture, $"{hidden.TrimStart()}\n");
        text.Append('\n');

        foreach (var file in answer.Files) ToolReply.ChurnRow(text, "", file);

        // Said under every rollup and not only a surprising one: a count that looks low beside the
        // files under it is the reading to head off, and an agent that adds the rows up to check has
        // already learnt the wrong fact. The lines do sum, which is why only the commits are said.
        if (answer.Depth is not null)
            text.Append("\nEach count is the distinct commits that touched anything beneath the directory, "
                        + "so it is not the sum of its files' counts; the lines are. "
                        + "Call again without depth, scoped to one of these, for the files in it.\n");

        if (coverage is not null) text.Append(CultureInfo.InvariantCulture, $"\n{coverage}\n");
        return text.ToString();
    }

    [McpServerTool(Name = "co_changed", ReadOnly = true, Idempotent = true,
        Title = "Find the files that usually change with a file")]
    [Description("""
                 Ranks the files that were committed alongside one file over a window of history, most shared commits first. Use it once you have found the file you need to change, to find what usually has to change with it: the coupling the code does not show — a constant and the places that read it, a stored procedure and its caller, two files that have simply always moved together.

                 - This is evidence and not proof. Two files in one reformat share a commit without sharing anything else, and the ranking says how many commits each pair shares so you can tell a habit from an accident.
                 - The window ends at the newest commit in the index, not at today, and the reply says which dates it covered.
                 - Commits that touched a great many paths at once are left out of the pairing: one reformat or vendor drop pairs every path it touched with every other and would swamp the answer. The reply says when that happened, and where a file has nothing but such commits it says there is no usable co-change history rather than that the file moves alone.
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
                   + $"could have changed alongside. {WhyNoCommits(answer.Recorded)}";

        // Before the empty ranking is read as a finding: where the ceiling excluded every commit the
        // file has in the window, nothing was paired and nothing could have been (#127). A file whose
        // only recorded change is a bulk rename has no co-change history to report, which is the
        // opposite of "nothing moves with it" — and the sentence below would have said "any of the 0
        // commits that touched it", a count that denies the commits the same reply is explaining.
        if (coupling.Files.Count == 0 && coupling.Paired == 0)
            return $"There is no usable co-change history for {spelled} between {window.Describe()}. "
                   + $"{ExcludedNote(coupling, answer.MaxCommitPaths)} That leaves nothing to pair it with, "
                   + "so this is not evidence that the file moves alone: a path whose only recorded commits "
                   + "are mass changes — a bulk rename, a reformat, an initial import — reaches this answer "
                   + "however strongly it is coupled. file_history lists those commits and commit_files says "
                   + "what one of them touched; blame is what still answers who changed these lines.";

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
    ///     Why a window reached none of a path's commits (#115). Two opposite facts share this branch:
    ///     a path the imported history has never recorded, which is what a bulk rename leaves behind and
    ///     which no window will ever reach, and a path whose commits are simply older than the days
    ///     asked for. They must not share a sentence — "raise days" is the whole answer to the second
    ///     and useless against the first, where the file's coupling is real and its history sits under
    ///     another name. History is matched by the path a commit recorded, which is what a caller has to
    ///     know to read either answer; blame follows content across a rename and can still answer.
    /// </summary>
    private static string WhyNoCommits(RecordedPath? recorded)
    {
        const string ByPath =
            "History here is matched by the path a commit recorded, so it begins where the file was last renamed.";

        if (recorded is not { Commits: > 0 } some)
            return $"{ByPath} No commit at all is recorded under this path, which is what an unrelated bulk "
                   + "rename leaves behind — a long-lived file that looks brand new. Widening days will not "
                   + "reach it; blame follows content across a rename and can still say who changed these lines.";

        string newest = some.Newest is { } at
            ? string.Create(CultureInfo.InvariantCulture, $", the newest from {at:yyyy-MM-dd}")
            : "";
        // No rename claim on this branch. The path HAS a history and the window simply missed it,
        // which is the opposite fact from the one above; saying "this is usually a rename" of a path
        // with four hundred recorded commits would put both facts back in one sentence.
        return string.Create(CultureInfo.InvariantCulture,
            $"{some.Commits} {ToolReply.Plural(some.Commits, "commit")} {ToolReply.Plural(some.Commits, "is", "are")} recorded under this path{newest}, all of them older than the window; raise days to reach them. {ByPath} blame follows content across a rename and is not bounded by the window.");
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

    /// <summary>
    ///     The same, where a <c>path</c> scope may have narrowed the read further (#118). The path wins
    ///     because it is the narrower of the two and names the repository already; naming both would
    ///     say the same slug twice in one sentence.
    /// </summary>
    private static string Scope(IndexedRepository? repository, PathScope? path) =>
        path is { } under ? $" under '{under.Spelled}'" : Scope(repository);

    /// <summary>
    ///     The caveat every path-scoped reply ends with. The scope is matched on the path a commit
    ///     recorded, so a directory reorganised last month has nothing recorded under its new name and
    ///     would otherwise read as a directory nobody works in — the same fact file_history states, and
    ///     the reason an empty scoped answer is never a finding about the people who work there.
    /// </summary>
    private static string ByRecordedPath(PathScope? path) =>
        path is null
            ? ""
            : NotAtHead(path)
              + " The scope is matched by the path each commit recorded, so it begins where a file was "
              + "last renamed; a directory that was moved records nothing under its new name.";

    /// <summary>
    ///     The sentence a scope that history records and HEAD no longer holds carries, and the whole
    ///     reason such a scope is answered rather than refused (#132). It leads the note, before the
    ///     recorded-path caveat, because it is a fact about this call and the caveat is a fact about
    ///     every path scope. <c>(no longer at HEAD)</c> is spelled the way <c>hot_files</c> and
    ///     <c>commit_files</c> spell it: a second wording of one fact is how an agent ends up believing
    ///     they are two.
    /// </summary>
    private static string NotAtHead(PathScope? path) =>
        path is { AtHead: false } gone
            // Said about the scope and never about the rows: the same note ends an empty page, and
            // "these are its commits" under a page past the end would be a sentence about no rows.
            ? $" Nothing is at '{gone.Spelled}' now (no longer at HEAD) — a later commit deleted it or "
              + "renamed it away, so there is nothing there to read; this scope is its recorded history."
            : "";

    /// <summary>
    ///     The same caveat under a listing rather than after a sentence — its own paragraph, because a
    ///     line of prose tacked onto the last row would read as part of the row. Written once: the two
    ///     listings that carry it must not drift into two wordings of one fact.
    /// </summary>
    private static void AppendRecordedPathNote(StringBuilder text, PathScope? path)
    {
        if (path is null) return;
        text.Append(CultureInfo.InvariantCulture, $"\n{ByRecordedPath(path).TrimStart()}\n");
    }
}
