using System.ComponentModel;
using System.Globalization;
using System.Text;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using ModelContextProtocol.Server;

namespace CodeExplorer.Search;

/// <summary>
///     The two tools that list commits: <c>git_log</c> and <c>authors</c>. They share a scope, a
///     filter and the sentence that says an author filter matched nobody, so their prose is written
///     beside itself rather than beside the reads about one file.
/// </summary>
internal sealed partial class HistoryTools
{
    [McpServerTool(Name = "git_log", ReadOnly = true, Idempotent = true, Title = "List the project's commits")]
    [Description("""
                 Lists commits of the project's default branch, newest first. Use it to see what changed recently, and how much of a project is moving, before asking about any one file.

                 - `repo` scopes it to one repository, `path` to a folder or a file inside one, `author` to one person, `message` to what a commit says it did; `limit` and `page` walk it. Those are its only arguments, and they combine.
                 - `path` is a qualified path, exactly as grep, glob and list_tree print one: `main/src/Api` for everything under a folder, or `main/src/Api/Orders.cs` for one file. It is what makes "what has been happening in this folder" one call. Scoping is by the path each commit recorded, so it begins where a file was last renamed. A path HEAD no longer holds is still scopeable: a file a later commit deleted, or a folder it renamed away, has commits to list and nothing to open, and the answer says so.
                 - `author` matches the email address, not the display name: `grace@example.com` or `grace`, never "Grace Hopper". `authors` lists the addresses.
                 - `message` matches text in the subject line, case-insensitively: a ticket key, a PR number, a release name. A ticket or PR number lives in the commit message and almost never in the code, so look for it here rather than with grep. It searches the subject only, not the body, and it is text and not a pattern — `%` and `_` match themselves.
                 - Nothing else filters — not by one commit and not by date. Any other argument name is named as ignored above the answer.
                 - It is not project-wide only: `repo` and `path` narrow it, and `file_history` is the same read for one exact path.
                 - A scope whose history was split by a rename says so: the reply names what the path was called before, the commits the whole chain accounts for, and the call that reads the earlier name. Renames are signalled, never followed — the count is still this path's alone.
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
        int limit = _defaultCommits,
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
        var outcome = await history.LogAsync(Bound.Slug, new LogRequest(repo, limit, page, author, message, path),
            cancellationToken);
        return ToolReply.Render<LogAnswer>(outcome, Log,
            answer => $"Narrow with repo, path or author, or raise page past {answer.Page}.");
    }

    /// <summary>
    ///     The commits, with the closing note after whichever shape the answer took. Applied here
    ///     rather than in each branch, because the branches that need the note most are the empty ones
    ///     and a note added branch by branch is a note the next branch forgets. It was appended in four
    ///     branches across this file and one in <c>file_history</c> until it was not, and each of the
    ///     five had to remember both the sentence and which of three ways to attach it (#131, #132).
    /// </summary>
    private static string Log(LogAnswer answer) =>
        PathNote.After(LogBody(answer), Noted(answer.HasHistory, answer.Path), PathNoteFor.GitLog);

    private static string LogBody(LogAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // Before the empty-page reading, because an address that matches nobody and a page past the
        // end of a real author's commits are opposite facts and the second sentence would fit both.
        if (answer.Author is { Addresses: 0 } miss) return NoSuchAuthor(miss, answer.Repository, answer.Path);

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
                           : $"No commits{By(answer.Author)} are recorded{where}.");

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Commits.Count} {ToolReply.Plural(answer.Commits.Count, "commit")}{By(answer.Author)}{Saying(answer.Message)}{where}, newest first:\n\n");
        foreach (var commit in answer.Commits) Append(text, commit, answer.Repository is null && answer.Path is null);
        if (answer.Author is { } filter) Matched(text, filter, answer.Commits.Count);
        return text.ToString();
    }

    /// <summary>
    ///     A filtered miss, which must not read as a clean negative: "no commits by Holger" and "no
    ///     address here contains Holger" mean opposite things to an agent, and the first is what an
    ///     unqualified empty answer says (CODING_STANDARDS, Errors). The count is what makes it
    ///     actionable — a project with authors and no match is a misspelling — and the rule it was
    ///     probably broken against is the one the name-shaped guess breaks.
    ///     It carries the <c>path</c> scope for the reason the count makes it actionable at all: the
    ///     addresses were counted under that scope, so a sentence that named only the repository would
    ///     offer a number of a narrower thing as the project's. A scope HEAD no longer holds has to be
    ///     said here too — a miss under a path that is gone must not read as a miss under a live one —
    ///     and that sentence is <see cref="PathNote" />'s, applied to every branch of
    ///     <see cref="LogBody" /> alike rather than remembered by each.
    /// </summary>
    private static string NoSuchAuthor(AuthorFilter filter, IndexedRepository? repository, PathScope? path) =>
        string.Create(CultureInfo.InvariantCulture,
            $"No address contains '{filter.Query}'{Scope(repository, path)} — `author` matches the address, not the name. {filter.AuthorsInScope} {ToolReply.Plural(filter.AuthorsInScope, "address", "addresses")} recorded; call authors to list them.");

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
                 - A scope whose history was split by a rename says so, naming the earlier path and the commits the whole chain accounts for. Renames are signalled, never followed.
                 - It says who touched the code, never who wrote it: a reformat is a commit, so a mass change makes its author look expert in files they only reindented.
                 """)]
    public async Task<string> Authors(
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        [Description("Authors to return, 1-200. Default 30.")]
        int limit = _defaultAuthors,
        [Description(
            "Qualified path of a folder or a file to scope to, e.g. \"main/src/Api\" or \"main/src/Api/Orders.cs\". Matched by the path each commit recorded, so it begins where a file was last renamed. A path HEAD no longer holds still scopes, because history recorded it. Default: the whole project.")]
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        return ToolReply.Render<AuthorsAnswer>(
            await history.AuthorsAsync(Bound.Slug, new AuthorsRequest(repo, limit, path), cancellationToken),
            AuthorList, "Lower limit to see fewer, or scope with repo or path.");
    }

    /// <summary>
    ///     The authors, with the closing note after whichever shape the answer took. Applied for the
    ///     reason <see cref="Log" /> gives: the empty branch is the one an agent most needs it on, and
    ///     it is the one a branch-by-branch note is likeliest to be missing from.
    /// </summary>
    private static string AuthorList(AuthorsAnswer answer) =>
        PathNote.After(AuthorListBody(answer), Noted(answer.HasHistory, answer.Path), PathNoteFor.Authors);

    /// <summary>
    ///     The scope these two reads note, or nothing where the project has no history. A path is
    ///     resolved before the history is looked for, so an index with none still carries the scope it
    ///     was asked about — and the caveat about how scoping matches recorded commit paths, said under
    ///     the sentence that no commits were recorded at all, would describe a read that never happened.
    ///     The three other reads need no such guard: neither the chain nor the gone sentence can be
    ///     filled from an index with no commits in it, and the caveat is theirs to state in their tool
    ///     description rather than in the reply.
    /// </summary>
    private static ScopeNote? Noted(bool hasHistory, PathScope? path) =>
        hasHistory ? ScopeNote.Of(path) : null;

    private static string AuthorListBody(AuthorsAnswer answer)
    {
        if (!answer.HasHistory) return ToolReply.NoHistory;
        // A project with history and no author is not reachable — a commit carries one — so this is
        // about a repository scope that holds no commits, and says that rather than "nobody".
        string where = Scope(answer.Repository, answer.Path);
        if (answer.Authors.Count == 0)
            return $"No commits are recorded{where}, so no authors are.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{answer.Total} {ToolReply.Plural(answer.Total, "author")}{where}, most commits first");
        // Only where the list was cut, so the common answer does not carry arithmetic nobody needs.
        text.Append(answer.Total > answer.Authors.Count
            ? string.Create(CultureInfo.InvariantCulture, $" ({answer.Authors.Count} shown, limit {answer.Limit}):\n\n")
            : ":\n\n");
        foreach (var author in answer.Authors) ToolReply.AuthorRow(text, "", author);
        return text.ToString();
    }

}
