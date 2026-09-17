using System.Globalization;
using System.Text;

namespace CodeExplorer;

/// <summary>
///     The reply shaping every index-backed tool shares: the size ceiling, the line clip and the
///     wording of a filtered miss. One place, so `grep` and `read_file` cannot drift into telling an
///     agent two different limits. The wording for a project that has nothing to answer from is
///     <see cref="IndexReader.NoIndex" />, because the reader is what decides there is nothing.
/// </summary>
internal static class ToolReply
{
    /// <summary>
    ///     Hard ceiling on one reply, roughly 10k tokens. A line cap alone is not enough: two hundred
    ///     ordinary hits, or one long file, still flood a context. An exact multiple of 1024, so the
    ///     "capped at N KB" the caller reads is the real number.
    /// </summary>
    public const int MaxOutputChars = 40 * 1024;

    /// <summary>
    ///     Longer than any hand-written source line, shorter than a minified bundle or a data literal,
    ///     which would otherwise spend the whole reply budget on one hit.
    /// </summary>
    public const int MaxLineChars = 500;

    /// <summary>
    ///     What every index-backed tool returns: the sentence a <see cref="Problem" /> already is, or
    ///     what the tool makes of its result, capped.
    ///     One chokepoint because the two halves were being spelled out at each of fifteen tool exits,
    ///     and the half that gets forgotten is the cap — a reply that overruns is not a worse answer,
    ///     it is an agent's whole context spent on one call. Going through here, a tool cannot return
    ///     without having decided on the advice for what it would cut.
    ///     The cast is safe while every query module answers with its one result type or a problem, and
    ///     throws rather than lies if one ever answers with something else.
    /// </summary>
    /// <param name="outcome">What the query module answered.</param>
    /// <param name="answer">What this tool makes of its result.</param>
    /// <param name="advice">How the caller gets the rest, said only if the cap bites.</param>
    public static string Render<T>(Outcome outcome, Func<T, string> answer, string advice) where T : Outcome =>
        Render(outcome, answer, _ => advice);

    /// <summary>
    ///     The same where the advice names something the answer knows — the page a caller has reached,
    ///     the limit it asked for. A separate overload rather than a lambda at every call site: four
    ///     tools in five have nothing to say that the result could tell them.
    /// </summary>
    public static string Render<T>(Outcome outcome, Func<T, string> answer, Func<T, string> advice)
        where T : Outcome
    {
        if (outcome is Problem problem) return problem.Explanation;
        var result = (T)outcome;
        return Cap(answer(result), advice(result));
    }

    /// <summary>
    ///     What every reply drawn from history says when there is none. Distinguishing this from
    ///     "nothing matched" is the whole point: an empty answer to "who changed this" reads as
    ///     "nobody", which is a fact, and this is the absence of one (CONTEXT.md, History). One
    ///     sentence and not one per surface, because two spellings of an absence are two facts to keep
    ///     in step and the overview and the history tools are read in the same session.
    /// </summary>
    public const string NoHistory =
        "This project's index holds no history, so no commit, author or date can be reported for it and "
        + "no file can be ranked by how much it changed. That is the case for an index built before "
        + "history was imported, and for one whose repositories could not be walked. Ask the operator to "
        + "refresh the project; the code itself is searchable meanwhile.";

    /// <summary>
    ///     One row of a churn ranking: the counts, then the path, then the mark saying there is nothing
    ///     at it to read any more. The overview and <c>hot_files</c> rank the same files over the same
    ///     window and a reader compares the two, so the row is drawn once — and the mark most of all,
    ///     because one surface forgetting it is an agent sent to open a file that is not there.
    /// </summary>
    /// <param name="text">The reply being built.</param>
    /// <param name="indent">What the surface puts before a row; the overview indents its sections.</param>
    /// <param name="file">The ranked file.</param>
    public static void ChurnRow(StringBuilder text, string indent, ChurnedFile file)
    {
        text.Append(indent);
        text.Append(CultureInfo.InvariantCulture,
            $"{file.Commits,4} {Plural(file.Commits, "commit"),-8} +{file.Added,-7:N0} -{file.Deleted,-7:N0} ");
        RankedPath(text, file.QualifiedPath, file.AtHead);
    }

    /// <summary>
    ///     Ends a ranked row: the path, and the mark saying there is nothing at it to read any more.
    ///     Every ranking drawn from history ranks paths a later commit deleted or renamed away, and two
    ///     spellings of that mark would be one of them eventually sending an agent to open a file that
    ///     is not there.
    /// </summary>
    public static void RankedPath(StringBuilder text, string qualifiedPath, bool atHead)
    {
        text.Append(qualifiedPath);
        if (!atHead) text.Append("  (no longer at HEAD)");
        text.Append('\n');
    }

    /// <summary>Trims a reply to the ceiling at a line boundary, saying how much was cut and how to get the rest.</summary>
    public static string Cap(string text, string advice)
    {
        if (text.Length <= MaxOutputChars) return text;

        int cut = text.LastIndexOf('\n', MaxOutputChars);
        if (cut < MaxOutputChars / 2) cut = MaxOutputChars;
        return text[..cut] + string.Create(CultureInfo.InvariantCulture,
                   $"\n\n... results truncated at {MaxOutputChars / 1024} KB ({text.Length - cut:N0} more characters). ") +
               advice + "\n";
    }

    public static string Clip(string text)
    {
        string trimmed = text.TrimEnd();
        return trimmed.Length <= MaxLineChars
            ? trimmed
            : string.Create(CultureInfo.InvariantCulture,
                $"{trimmed[..MaxLineChars]} ... [{trimmed.Length - MaxLineChars} more characters on this line]");
    }

    public static string Plural(long n, string one, string? many = null) => n == 1 ? one : many ?? one + "s";

    /// <summary>
    ///     The one sentence every filtered tool ends a miss with. Written once because a filtered miss
    ///     and a clean negative read identically to an agent and mean opposite things, and a tool that
    ///     spelled the warning its own way would be the one an agent skims past.
    ///     The filters are not named, because the tools do not share a set: grep spans every repository
    ///     and has no repo filter, and a sentence naming one it lacks would be a false lead.
    /// </summary>
    /// <param name="count">How many files the filters hid.</param>
    public static string HiddenByFilters(long count) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{count} {Plural(count, "file")} outside your filters; the filters hid every match. Widen or drop them to see those.");

    /// <summary>
    ///     The other half of <see cref="HiddenByFilters" />: matches were shown, and the filters hid
    ///     further ones. Said in one place because a thin answer whose declaration sits in an excluded
    ///     file is the footgun every index search shares, and two spellings of the warning would let
    ///     one tool sound more certain than another about the same thing.
    /// </summary>
    public static string PartlyHiddenByFilters(long count) =>
        string.Create(CultureInfo.InvariantCulture,
            $"your filters hid {count} further matching {Plural(count, "file")}. A declaration you cannot see may be in one of them. Re-run without them to check.");

    public static string Bytes(long size) => size switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{size} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{size / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{size / (1024.0 * 1024.0):0.#} MB")
    };
}
