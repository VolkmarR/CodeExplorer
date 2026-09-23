using System.ComponentModel;
using System.Globalization;
using System.Text;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using ModelContextProtocol.Server;

namespace CodeExplorer.Search;

/// <summary>
///     <c>co_changed</c>, and the four sentences it answers with when nothing pairs. They are four
///     because they mean opposite things to an agent, and they are here rather than beside the other
///     tools because no other reply has to tell them apart.
/// </summary>
internal sealed partial class HistoryTools
{
    [McpServerTool(Name = "co_changed", ReadOnly = true, Idempotent = true,
        Title = "Find the files that usually change with a file")]
    [Description("""
                 Ranks the files that were committed alongside one file over a window of history, most shared commits first. Use it once you have found the file you need to change, to find what usually has to change with it: the coupling the code does not show — a constant and the places that read it, a stored procedure and its caller, two files that have simply always moved together.

                 - This is evidence and not proof. Two files in one reformat share a commit without sharing anything else, and the ranking says how many commits each pair shares so you can tell a habit from an accident.
                 - The window ends at the newest commit in the index, not at today, and the reply says which dates it covered.
                 - Commits that touched a great many paths at once are left out of the pairing: one reformat or vendor drop pairs every path it touched with every other and would swamp the answer. The reply says when that happened, and where a file has nothing but such commits it says there is no usable co-change history rather than that the file moves alone.
                 - Pairing never crosses a repository, because a commit does not.
                 - IMPORTANT: this is the one read here that FOLLOWS a rename instead of signalling it. An anchor that reached its path by a move is paired over its earlier paths too, so coupling survives the move and the ranking is one ranking. Every other path-scoped tool — git_log, authors, file_history, hot_files — counts only what its own path recorded, and says so; do not carry this tool's behaviour across to them.
                 - In the commit that moved the anchor, the files that moved WITH it are not counted as coupled — "these moved together" is one commit and not a relationship. Files that commit actually edited are counted, because a rename that updates its callers is the strongest coupling evidence there is.
                 - A path HEAD no longer holds is refused. The coupling of a file that is gone is a question about a path rather than about a file you can open; git_log and file_history still list its commits.
                 """)]
    public async Task<string> CoChanged(
        [Description("Qualified path of one file, e.g. \"main/src/Api/Foo.cs\".")]
        string path,
        [Description("Days back from the newest recorded commit, 1-3650. Default 90.")]
        int days = HistoryWindow.DefaultDays,
        [Description("Files to return, 1-100. Default 20.")]
        int limit = _defaultRankedFiles,
        CancellationToken cancellationToken = default)
    {
        string project = Bound.Slug;
        return ToolReply.Render<CoChangeAnswer>(
            await history.CoChangedAsync(project, new CoChangeRequest(path, days, limit), cancellationToken),
            answer => Coupling(answer, project), "Lower limit to see fewer.");
    }

    /// <summary>
    ///     The coupling answer, with the previous-path note after whichever of its six shapes was
    ///     reached. Wrapped rather than repeated in each branch: the thin answers are where the note
    ///     matters most, and a note added branch by branch is a note the next branch forgets. Nothing is
    ///     appended where the scope has no previous path, so every other reply is what it was.
    ///     The call it spells out is git_log's and file_history's, not its own: co_changed refuses a
    ///     path HEAD no longer holds by design (#136), and a previous path is one by definition.
    /// </summary>
    private static string Coupling(CoChangeAnswer answer, string projectSlug) =>
        PathNote.After(CouplingBody(answer, projectSlug), new ScopeNote(answer.Lineage),
            answer.Coupling.Files.Count > 0 ? PathNoteFor.CoChangedRanking : PathNoteFor.CoChangedThin);

    private static string CouplingBody(CoChangeAnswer answer, string projectSlug)
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
                   // Not "that history begins where the file was last renamed" any more: this tool's
                   // pairing follows the chain (#143), so a rename is the one explanation the caller
                   // can rule out here — and offering it would send them after something already done.
                   + "and not proof: the window is what bounds it, and only commits recorded there were paired."
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
        const string byPath =
            "History here is matched by the path a commit recorded, so it begins where the file was last renamed.";

        if (recorded is not { Commits: > 0 } some)
            return $"{byPath} No commit at all is recorded under this path, which is what an unrelated bulk "
                   + "rename leaves behind — a long-lived file that looks brand new. Widening days will not "
                   + "reach it; blame follows content across a rename and can still say who changed these lines.";

        string newest = some.Newest is { } at
            ? string.Create(CultureInfo.InvariantCulture, $", the newest from {at:yyyy-MM-dd}")
            : "";
        // No rename claim on this branch. The path HAS a history and the window simply missed it,
        // which is the opposite fact from the one above; saying "this is usually a rename" of a path
        // with four hundred recorded commits would put both facts back in one sentence.
        return string.Create(CultureInfo.InvariantCulture,
            $"{some.Commits} {ToolReply.Plural(some.Commits, "commit")} {ToolReply.Plural(some.Commits, "is", "are")} recorded under this path{newest}, all of them older than the window; raise days to reach them. {byPath} blame follows content across a rename and is not bounded by the window.");
    }

    /// <summary>
    ///     What the ceiling kept out, in one sentence, for a caller that has already established there
    ///     was something. Said rather than dropped: a ranking drawn from a third of a file's commits
    ///     without saying so is the one that misleads, and the number is also how a caller learns the
    ///     ceiling is wrong for their repository.
    ///     The sentence carries no leading separator, because the two callers want different ones.
    ///     One reason and one remedy, still. Following renames (#143) drops the paths a move carried
    ///     rather than the commit that carried them, so a rename never lands in this count — which is
    ///     what lets the remedy stay attached to the only cause that has one.
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

}
