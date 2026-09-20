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

    /// <summary>
    ///     The caveat every path-scoped reply ends with. The scope is matched on the path a commit
    ///     recorded, so a directory reorganised last month has nothing recorded under its new name and
    ///     would otherwise read as a directory nobody works in — the same fact file_history states, and
    ///     the reason an empty scoped answer is never a finding about the people who work there.
    /// </summary>
    private static string ByRecordedPath(PathScope? path, Func<PreviousPath, string> call) =>
        path is null
            ? ""
            : NotAtHead(path)
              + " The scope is matched by the path each commit recorded, so it begins where a file was "
              + "last renamed; a directory that was moved records nothing under its new name."
              // Where this index can say what the previous name was, the general caveat above is
              // followed by the particular fact, which is the one worth acting on (#131).
              + (PreviousPathNote(path.Lineage, call) is { Length: > 0 } chain ? " " + chain : "");

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
    private static void AppendRecordedPathNote(StringBuilder text, PathScope? path,
        Func<PreviousPath, string> call)
    {
        if (path is null) return;
        text.Append(CultureInfo.InvariantCulture, $"\n{ByRecordedPath(path, call).TrimStart()}\n");
    }

    /// <summary>
    ///     The call each reply spells out against a previous path. Four of the five name their own tool,
    ///     because the previous path is a path they answer for. <c>co_changed</c> does not: a path HEAD
    ///     no longer holds is refused there by design (#136), so recommending its own call would hand an
    ///     agent one specified never to work — it redirects to the same two reads its refusal names, and
    ///     an agent meets one story from both directions.
    /// </summary>
    private static Func<PreviousPath, string> Reads(string tool) =>
        previous => Literal + $"Call {tool} with path=\"{previous.Spelled}\" to read it.";

    /// <inheritdoc cref="Reads" />
    private static readonly Func<PreviousPath, string> RanksPrevious =
        previous => Literal + $"Call hot_files with directory=\"{previous.Spelled}\" to rank it.";

    /// <summary>
    ///     What the count above the note means for the four tools that do not follow a rename. It is
    ///     theirs and not the note's, because <c>co_changed</c> is now the one tool for which it is
    ///     false (#143).
    /// </summary>
    private const string Literal =
        "the count above is this path's alone, because scoping is by the path each commit recorded "
        + "and renames are signalled here, not followed. ";

    /// <inheritdoc cref="Reads" />
    /// <remarks>
    ///     No longer a redirect (#143). It sent the caller to git_log and file_history while the earlier
    ///     path was unpairable here; now the pairing spans the chain, so sending anyone elsewhere would
    ///     be telling them to fetch what they already have. What it says instead is that this answer is
    ///     wider than every other path-scoped one, because a reader who knows the rule would otherwise
    ///     read this one as literal too.
    ///     Two forms, chosen by whether there is a ranking. "The ranking above spans that chain" is
    ///     false on the thin branches — a window that reached no commit, or a file whose every commit
    ///     was a mass change — and those replies end by telling the caller to raise <c>days</c> or to
    ///     read <c>file_history</c>. A note claiming there was nothing more to do would contradict the
    ///     sentence directly above it.
    ///     Both forms say "within the window", because the pairing is still bounded by it: a file moved
    ///     two years ago and asked about over ninety days has a chain that no commit in the window
    ///     reaches, and a flat claim that the earlier path was paired in would be wrong exactly there.
    /// </remarks>
    private static Func<PreviousPath, string> SpansTheChain(bool ranked) =>
        previous => ranked
            ? $"The ranking above spans that chain, within the window it names — commits recorded under "
              + $"'{previous.Spelled}' are paired into it — so there is no second call to make. co_changed is "
              + "the one read here that follows a rename rather than signalling it; every other path-scoped "
              + "answer counts what its own path recorded."
            : $"The pairing did span that chain, within the window above: commits recorded under "
              + $"'{previous.Spelled}' were looked at too, so what is missing here is not the rename. "
              + "co_changed is the one read here that follows a rename rather than signalling it; every "
              + "other path-scoped answer counts what its own path recorded.";

    /// <summary>
    ///     What a scope was called before, said without being asked (#131). A scope whose history was
    ///     split by a rename answers for its post-rename slice alone — measured at 9 commits reported
    ///     for a directory with 1,252 — and nothing in the reply used to suggest the rest was there.
    ///     Three things, and all three or it is not worth saying: the previous path, the combined total
    ///     across the chain, and the call that reads the previous path. Naming the path and withholding
    ///     the total reproduces the original fault one level up, and describing the reads rather than
    ///     spelling the call leaves the work with the reader.
    ///     Empty for a scope with no previous path, which is what keeps every other reply byte-for-byte
    ///     what it was.
    /// </summary>
    /// <param name="lineage">The chain the scope's query found, or null where there is none.</param>
    /// <param name="tail">
    ///     Everything after the combined total: what the count above means, and what to do about it.
    ///     Both halves belong to the caller and not to this method, because they differ in kind and not
    ///     only in wording — four tools report a literal count and name the call that reads the rest,
    ///     while <c>co_changed</c> reports a count already taken across the chain and has nothing to
    ///     send anyone to. A fixed "the count above is this path's alone" here would have contradicted
    ///     the sentence the caller appended straight after it (#143).
    /// </param>
    private static string PreviousPathNote(PathLineage? lineage, Func<PreviousPath, string> tail)
    {
        if (lineage is not { Previous.Count: > 0 } chain) return "";

        var text = new StringBuilder();
        var first = chain.Previous[0];
        // "recorded under it" and not "further commits": this count is every commit at or under the
        // earlier path, and the rename itself touched both sides, so the two counts overlap by at least
        // one. The combined total below de-duplicates; a sentence claiming these were all additional
        // would be arithmetic the next sentence contradicts.
        text.Append(CultureInfo.InvariantCulture,
            $"This scope was renamed: its content was at '{first.Spelled}' before, where {first.Commits} "
            + $"{ToolReply.Plural(first.Commits, "commit")} {ToolReply.Plural(first.Commits, "is", "are")} recorded. ");
        // Every hop after the first, oldest last. A path renamed twice has a chain and an agent that
        // read only the newest hop would stop one rename short of the history it asked for.
        foreach (var older in chain.Previous.Skip(1))
            text.Append(CultureInfo.InvariantCulture,
                $"Before that, '{older.Spelled}' ({older.Commits} {ToolReply.Plural(older.Commits, "commit")}). ");
        if (chain.Omitted > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"{chain.Omitted} further earlier {ToolReply.Plural(chain.Omitted, "path")} not shown. ");

        // The number the caller actually wanted, and the reason a path alone is not an answer.
        int total = chain.CombinedCommits;
        text.Append(CultureInfo.InvariantCulture,
            $"{total} {ToolReply.Plural(total, "commit")} {ToolReply.Plural(total, "is", "are")} recorded across the whole chain; ");
        text.Append(tail(first));
        return text.ToString();
    }

    /// <summary>
    ///     A finished reply with the previous-path note after it, as its own paragraph — a line of prose
    ///     tacked onto the last row would read as part of the row. Applied to the whole reply rather
    ///     than inside each branch, because the branches that need the note most are the empty ones and
    ///     a note added branch by branch is a note the next branch forgets.
    ///     A scope with no previous path gets back exactly what it had, which is what keeps every other
    ///     reply byte-for-byte what it was.
    /// </summary>
    private static string WithPreviousPath(string reply, PathLineage? lineage, Func<PreviousPath, string> call) =>
        PreviousPathNote(lineage, call) is { Length: > 0 } note
            ? reply.TrimEnd('\n') + "\n\n" + note + "\n"
            : reply;

}
