using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The history tools that answer about one file or one commit — <c>file_history</c>,
///     <c>blame</c>, <c>commit</c> and <c>commit_files</c> — and <c>hot_files</c>, which ranks the
///     same changes rather than listing them. Each says in its own words that attribution is who
///     touched a line last and not who wrote it (CONTEXT.md, Attribution).
/// </summary>
internal sealed partial class HistoryTools
{
    [McpServerTool(Name = "file_history", ReadOnly = true, Idempotent = true,
        Title = "List the commits that changed a file")]
    [Description("""
                 Lists the commits that changed one file, newest first. This is the tool for "who has worked on this" and "when was this last touched".

                 - The path is qualified, exactly as grep and read_file print it: `main/src/Api/Foo.cs`.
                 - History is matched by path, so it begins where the file was last renamed or moved. An empty or short answer on an old file usually means a move, not that nobody touched it — the content's line-by-line history survives a move and is what blame reports.
                 - A path HEAD no longer holds is still listed: a file a later commit deleted, or renamed away, has commits to list and nothing to open, and the answer says so. blame and co_changed refuse such a path, because their answers are about the file that is there.
                 - A file that reached this path by a rename says so: the reply names what it was called before, the commits the whole chain accounts for, and the call that reads the earlier name.
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
            await history.FileHistoryAsync(Bound.Slug, new FileHistoryRequest(path, limit), cancellationToken),
            Changes, "Lower limit to see fewer.");
    }

    /// <summary>
    ///     The commits, with the previous-path note after whichever shape the answer took. Wrapped for
    ///     the reason <see cref="Coupling" /> is: the branch that matters most here is the empty one,
    ///     which tells the reader its history "reached this path by a rename" — and used to drop the
    ///     very rename it was describing, along with the count and the call (#131).
    /// </summary>
    private static string Changes(FileHistoryAnswer answer) =>
        WithPreviousPath(ChangesBody(answer), answer.Path.Lineage, Reads("file_history"));

    private static string ChangesBody(FileHistoryAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;

        string spelled = answer.Path.Spelled;
        // Only reachable for a path HEAD holds: one it does not is here because a commit recorded it,
        // so it has commits by construction.
        if (answer.Commits.Count == 0)
            return $"No commit in the recorded history changed '{spelled}'. "
                   + "The file is in the index, so this means its history is older than what was imported, or it "
                   + "reached this path by a rename — try blame, which follows the content rather than the path.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Commits.Count} {ToolReply.Plural(answer.Commits.Count, "commit")} changed {spelled}, newest first:\n\n");
        foreach (var commit in answer.Commits) Append(text, commit, false);
        // The sentence git_log and authors end a gone scope with, word for word. One fact, one wording:
        // a second spelling of it is how an agent ends up believing there are two.
        if (NotAtHead(answer.Path) is { Length: > 0 } gone)
            text.Append(CultureInfo.InvariantCulture, $"\n{gone.TrimStart()}\n");
        return text.ToString();
    }

    [McpServerTool(Name = "blame", ReadOnly = true, Idempotent = true, Title = "Show which commit last changed each line")]
    [Description("""
                 Shows which commit last changed each line of a file, grouped into runs of consecutive lines. Use it after a grep to find out who to ask about a specific line.

                 - This is who touched a line LAST, not who wrote the logic. A reformat, a rename or a whitespace fix is a change, and it becomes the answer. Treat a result as "ask this person", never as "this person introduced it".
                 - It cannot say when something was introduced: only the current content is indexed, so a line that was moved or reformatted points at that change and not at the original one.
                 - Lines with no commit are ones the build could not attribute; they are reported as such rather than left out.
                 - A path HEAD no longer holds is refused: attribution is the lines of the file as of the newest recorded commit, and a deleted or renamed-away path has none. git_log and file_history still list its commits.
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
            await history.BlameAsync(Bound.Slug, new BlameRequest(path, startLine, endLine), cancellationToken),
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
            await history.CommitAsync(Bound.Slug, new CommitRequest(sha), cancellationToken), Record,
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
            await history.CommitFilesAsync(Bound.Slug, new CommitFilesRequest(sha), cancellationToken),
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
                 - Scope it with `directory`, a qualified path: `main/src/Api` for one area, or a bare repository slug for one repository. Scoping is by the path each commit recorded, so it begins where a directory was last renamed or moved — a folder that was moved reads as a quiet one unless you know that.
                 - Ranking is by number of commits, then by lines changed. A reformat counts as a change, the same way blame does — this is where work happened, not where the logic changed.
                 - Files a later commit deleted or renamed away are ranked too and marked; there is nothing at those paths to read now.
                 - A directory whose history was split by a rename says so: the reply names what it was called before, the commits the whole chain accounts for, and the call that ranks the earlier name. The ranking itself is unchanged — renames are signalled, never followed.
                 - `depth` ranks **directories** instead of files. Reach for it when the question is which module, package or app is moving rather than which file — that is one call, where ranking files and then scoping the tool to each candidate directory in turn is one call per directory. Then call it again without `depth`, scoped to the directory that won, for the files inside it.
                 - IMPORTANT: a machine-authored commit counts exactly like a hand-written one. Regenerated output, a mechanical version bump across unrelated modules and a bulk rename all rank like real work, and a directory of generated files can outrank the code that generates it. `exclude` is the answer, in grep's syntax: `exclude="*.g.ts,*.generated.*,/migrations/,package-lock.json"`. Nothing is excluded by default and no naming convention is assumed — look at the top of an unfiltered ranking first, then exclude what the project turns out to regenerate. The reply says how many paths the filter hid.
                 """)]
    public async Task<string> HotFiles(
        [Description("Days back from the newest recorded commit, 1-3650. Default 90.")]
        int days = HistoryWindow.DefaultDays,
        [Description(
            "Qualified path of a directory to rank within, e.g. \"main/src/Api\", or a repository slug alone for one repository. Matched by the path each commit recorded, so it begins where a directory was last renamed or moved. Default: the whole project.")]
        string? directory = null,
        [Description("Rows to return — files, or directories under `depth` — 1-100. Default 20.")]
        int limit = DefaultRankedFiles,
        [Description(
            "Rank directories instead of files, grouped by this many path segments beneath the scope, 1-10. Unscoped, that is the first segments of the qualified path, so in a multi-repository project depth 1 ranks repositories and depth 2 their top-level directories. A directory's count is the commits that touched anything beneath it, each counted once. Default: rank files.")]
        int? depth = null,
        [Description(
            "Drop files whose qualified path matches any of these comma-separated terms, e.g. \"*.g.cs,/migrations/\". Same syntax as grep: a term with * or ? is a glob over the whole qualified path, anything else a plain substring, and matching is case-insensitive. Default: rank everything.")]
        string? exclude = null,
        [Description(
            "Rank ONLY files with these extensions, comma-separated: \"cs,ts\" or \".cs,.ts\". The other polarity of `exclude` and usually the shorter question: where a project's churn is dominated by project files and translations, naming the two or three extensions that are the code beats listing what is not. The reply lists the extensions the window actually holds, most-changed first, so a first unfiltered call shows what there is to ask for. Default: rank every extension.")]
        string? extensions = null,
        CancellationToken cancellationToken = default)
    {
        string project = Bound.Slug;
        return ToolReply.Render<ChurnAnswer>(
            await history.ChurnAsync(project, new ChurnRequest(directory, days, limit, depth, exclude, extensions),
                cancellationToken),
            answer => Ranking(answer, project),
            "Lower limit, narrow with directory, or roll the ranking up with depth.");
    }

    /// <summary>
    ///     The ranking, with the previous-path note after whichever shape the answer took. The empty
    ///     branches are the ones this exists for: a directory renamed longer ago than the window reports
    ///     no rows and says "raise days to look further back", and a scope whose whole history sits
    ///     under its previous name has no window at all. Both read as a quiet directory, which is the
    ///     fault #131 is — so a note appended only under a ranking would miss the two answers that need
    ///     it most.
    /// </summary>
    private static string Ranking(ChurnAnswer answer, string projectSlug) =>
        WithPreviousPath(RankingBody(answer, projectSlug), answer.Lineage, RanksPrevious);

    private static string RankingBody(ChurnAnswer answer, string projectSlug)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // The project has history and this scope has none: a repository whose walk found nothing, which
        // reads as "nobody has changed it" unless it is said outright. This answer names the scope it
        // is about, so the coverage caveat below would only repeat it — and is not paid for here.
        if (answer.Window is not { } window) return NoCommitsIn(answer.ScopeSpelled, projectSlug);

        // Said whether or not there is a ranking, and more when there is not: a reader shown nothing is
        // the one most likely to conclude that nothing changed.
        string? coverage = Coverage(answer.Coverage);

        // What the filters kept out, said wherever there is an answer to misread: a filtered ranking
        // must never read as an unfiltered one, and an empty filtered ranking must never read as a
        // scope where nothing changed.
        string hidden = answer.Hidden == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $" The filter hid {answer.Hidden} {ToolReply.Plural(answer.Hidden, "path")}, so this is a filtered ranking.");

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

        // What the window is written in, under the ranking rather than over it (#161). An agent that
        // has just read twenty `.csproj` rows needs to know that `extensions` is the one call that
        // fixes it, and needs the spellings this scope actually holds rather than the ones it would
        // guess — a project whose code is `.prg` is exactly the project where guessing `.cs` returns
        // an empty ranking that reads as a quiet repository.
        if (answer.Extensions.Count > 1) Extensions(text, answer.Extensions);

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

    /// <summary>
    ///     The extensions the scope holds, most-changed first, as the menu for the <c>extensions</c>
    ///     argument (#161). Written only where there is more than one, because a scope written in one
    ///     extension offers no choice and the line would be noise on every reply.
    ///     The counts are commits and paths, both: a handful of generated files accounting for
    ///     hundreds of commits is exactly the shape this line exists to make visible, and the commit
    ///     count alone reads as a busy area rather than as a busy generator.
    /// </summary>
    private static void Extensions(StringBuilder text, IReadOnlyList<ChurnedExtension> extensions)
    {
        text.Append("\nExtensions changed in this window, most commits first — pass any of these as ")
            .Append("`extensions` to rank only those:\n  ");
        text.AppendJoin(", ", extensions.Select(e => string.Create(CultureInfo.InvariantCulture,
            // The empty extension is a real answer and needs a name a reader can read; it is not a
            // spelling anyone can pass back, so it says so rather than printing as nothing at all.
            $"{(e.Extension.Length == 0 ? "(no extension)" : e.Extension)} {e.Commits}c/{e.Files}f")));
        text.Append('\n');
    }
}
