using System.Globalization;
using System.Text;

namespace CodeExplorer;

/// <summary>
///     What a path-scoped reply's closing note is written from: the chain the scope's query found, and
///     the spelling to name it by where HEAD no longer holds it.
///     Two fields and not three, because the two are one fact: the scope is spelled out only ever
///     <em>because</em> it is gone, so a <see cref="GoneFrom" /> that is null is a scope still at HEAD
///     and there is no way to hold "gone" without the name to say it with.
///     It exists because the five reads that carry a note carry their scope in three shapes — a
///     nullable <see cref="PathScope" /> (<c>git_log</c>, <c>authors</c>), a required one
///     (<c>file_history</c>), and a bare <see cref="PathLineage" /> beside a spelled scope
///     (<c>hot_files</c>, <c>co_changed</c>, neither of which can be asked about a path HEAD has lost).
///     Naming the three here is what lets one call close every one of those replies; folding it into
///     <see cref="ToolReply" />'s render so no tool decides at all wants those three shapes made one
///     first, which is a change to five answer records and not to this.
/// </summary>
/// <param name="Lineage">The chain the scope's query found, or null where nothing leads back.</param>
/// <param name="GoneFrom">
///     What the scope is spelled as, where a later commit deleted or renamed it away; null for a scope
///     HEAD still holds.
/// </param>
public sealed record ScopeNote(PathLineage? Lineage, string? GoneFrom = null)
{
    /// <summary>The note a <see cref="PathScope" /> is owed, or null where the call named no path.</summary>
    public static ScopeNote? Of(PathScope? scope) =>
        scope is null ? null : new ScopeNote(scope.Lineage, scope.AtHead ? null : scope.Spelled);
}

/// <summary>
///     Which read a note is being written for. The two <c>co_changed</c> cases are two different
///     sentences and not one sentence with a flag: a ranking is told its chain was already paired in,
///     and a thin answer is told the rename is not what is missing — and a reply that ends "raise days"
///     cannot also claim there was nothing more to do (#143).
/// </summary>
public enum PathNoteFor
{
    GitLog,
    Authors,
    FileHistory,
    HotFiles,
    CoChangedRanking,
    CoChangedThin,
}

/// <summary>
///     The closing note every path-scoped reply carries, written in one place and appended in one.
///     <para>
///         It is one entry point because it used to be three, and the three differed in where they put
///         the note rather than in what it said: concatenated onto a sentence, appended under a
///         listing, or wrapped around the whole reply. The first two were the same text with different
///         whitespace, and the third was applied outside the branch tree for the reason both
///         <c>co_changed</c> and <c>hot_files</c> record — a note added branch by branch is a note the
///         next branch forgets, which is the fault <c>git_log</c>'s author miss was caught having
///         (#131, #132, #143).
///     </para>
///     <para>
///         So the note is always a trailing paragraph and always applied to a finished body. A branch
///         cannot forget it, because no branch applies it.
///     </para>
/// </summary>
internal static class PathNote
{
    /// <summary>
    ///     What the count above the note means for the four reads that do not follow a rename. It is
    ///     theirs and not the note's, because <c>co_changed</c> is the one read for which it is false
    ///     (#143).
    /// </summary>
    private const string Literal =
        "the count above is this path's alone, because scoping is by the path each commit recorded "
        + "and renames are signalled here, not followed. ";

    /// <summary>
    ///     The general caveat, said by the two reads that do not say it in their tool description. A
    ///     scope is matched on the path a commit recorded, so a directory reorganised last month has
    ///     nothing recorded under its new name and would otherwise read as a directory nobody works in.
    ///     <c>hot_files</c> states this in its <c>directory</c> argument's description instead, where a
    ///     model reads it before calling rather than after, and <c>file_history</c> answers for one
    ///     exact path and says it in the branch where it bites. Two of five, and which two is a
    ///     decision per read rather than a sentence some replies drifted out of.
    /// </summary>
    private const string ByRecordedPath =
        "The scope is matched by the path each commit recorded, so it begins where a file was "
        + "last renamed; a directory that was moved records nothing under its new name.";

    /// <summary>
    ///     The finished reply with its closing note after it, as its own paragraph — a line of prose
    ///     tacked onto the last row would read as part of the row.
    ///     A scope with nothing to note gets back exactly what it was handed, which is what keeps every
    ///     other reply byte-for-byte what it was.
    /// </summary>
    /// <param name="body">The reply as its tool finished it, whichever shape it took.</param>
    /// <param name="scope">What the note is written from, or null where the call named no path.</param>
    /// <param name="tool">Which read this is, which decides the caveat and the call the note spells out.</param>
    public static string After(string body, ScopeNote? scope, PathNoteFor tool)
    {
        if (scope is null) return body;

        var note = new StringBuilder();
        // The gone sentence leads, because it is a fact about this call and the caveat below it is a
        // fact about every path scope.
        if (scope.GoneFrom is { } gone) note.Append(NotAtHead(gone));
        if (SaysRecordedPath(tool)) Space(note).Append(ByRecordedPath);
        // Where this index can say what the previous name was, the general caveat is followed by the
        // particular fact, which is the one worth acting on (#131).
        if (Chain(scope.Lineage, tool) is { Length: > 0 } chain) Space(note).Append(chain);

        return note.Length == 0 ? body : body.TrimEnd('\n') + "\n\n" + note + "\n";
    }

    private static StringBuilder Space(StringBuilder note) => note.Length == 0 ? note : note.Append(' ');

    /// <summary>
    ///     The sentence a scope that history records and HEAD no longer holds carries, and the whole
    ///     reason such a scope is answered rather than refused (#132). <c>(no longer at HEAD)</c> is
    ///     spelled the way <c>hot_files</c> and <c>commit_files</c> spell it: a second wording of one
    ///     fact is how an agent ends up believing they are two.
    /// </summary>
    private static string NotAtHead(string spelled) =>
        // Said about the scope and never about the rows: the same note ends an empty page, and "these
        // are its commits" under a page past the end would be a sentence about no rows.
        $"Nothing is at '{spelled}' now (no longer at HEAD) — a later commit deleted it or "
        + "renamed it away, so there is nothing there to read; this scope is its recorded history.";

    /// <summary>Which reads carry the general caveat in the reply rather than in their description.</summary>
    private static bool SaysRecordedPath(PathNoteFor tool) =>
        tool is PathNoteFor.GitLog or PathNoteFor.Authors;

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
    private static string Chain(PathLineage? lineage, PathNoteFor tool)
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
        text.Append(Tail(tool, first));
        return text.ToString();
    }

    /// <summary>
    ///     Everything after the combined total: what the count above means, and what to do about it.
    ///     A table and not a parameter, because the differences are per read and were already a table —
    ///     spread across three signatures a caller had to choose between.
    ///     Four of the six name their own tool, because the previous path is a path they answer for.
    ///     <c>co_changed</c> does not: a path HEAD no longer holds is refused there by design (#136), so
    ///     recommending its own call would hand an agent one specified never to work.
    /// </summary>
    private static string Tail(PathNoteFor tool, PreviousPath previous) => tool switch
    {
        PathNoteFor.GitLog => Reads("git_log", previous),
        PathNoteFor.Authors => Reads("authors", previous),
        PathNoteFor.FileHistory => Reads("file_history", previous),
        PathNoteFor.HotFiles => Literal + $"Call hot_files with directory=\"{previous.Spelled}\" to rank it.",
        // No longer a redirect (#143). It sent the caller to git_log and file_history while the earlier
        // path was unpairable here; now the pairing spans the chain, so sending anyone elsewhere would
        // be telling them to fetch what they already have. What it says instead is that this answer is
        // wider than every other path-scoped one, because a reader who knows the rule would otherwise
        // read this one as literal too.
        // Both forms say "within the window", because the pairing is still bounded by it: a file moved
        // two years ago and asked about over ninety days has a chain that no commit in the window
        // reaches, and a flat claim that the earlier path was paired in would be wrong exactly there.
        PathNoteFor.CoChangedRanking =>
            $"The ranking above spans that chain, within the window it names — commits recorded under "
            + $"'{previous.Spelled}' are paired into it — so there is no second call to make. co_changed is "
            + "the one read here that follows a rename rather than signalling it; every other path-scoped "
            + "answer counts what its own path recorded.",
        // "The ranking above spans that chain" is false on the thin branches — a window that reached no
        // commit, or a file whose every commit was a mass change — and those replies end by telling the
        // caller to raise days or to read file_history. A note claiming there was nothing more to do
        // would contradict the sentence directly above it.
        PathNoteFor.CoChangedThin =>
            $"The pairing did span that chain, within the window above: commits recorded under "
            + $"'{previous.Spelled}' were looked at too, so what is missing here is not the rename. "
            + "co_changed is the one read here that follows a rename rather than signalling it; every "
            + "other path-scoped answer counts what its own path recorded.",
        // Unreachable: every member above is named. The arm exists because CS8524 counts a cast int as
        // an uncovered value and this build treats it as an error, so an exhaustive switch cannot be
        // written without one. It is therefore not a failure a caller can meet and not an McpException
        // (CODING_STANDARDS, Errors) — what stops a seventh read reaching it is
        // PathNoteTests.Every_read_has_a_tail, which fails the moment one is added without its prose.
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    private static string Reads(string tool, PreviousPath previous) =>
        Literal + $"Call {tool} with path=\"{previous.Spelled}\" to read it.";
}
