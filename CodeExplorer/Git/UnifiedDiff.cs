namespace CodeExplorer.Git;

/// <summary>
///     One edit a commit made to a file, as the position it applies at in the file before the commit
///     and how many lines it took out and put in. A patch is a list of these in ascending order, and
///     replaying them onto the previous attribution is how a build attributes without a blame
///     (ADR-0007). Context lines are not carried: they are the lines between edits, and the replay
///     copies them by position.
/// </summary>
/// <param name="OldLine">
///     0-based index into the file before the commit of the first line removed — or, for a pure
///     insertion, of the line the new ones go in front of; equal to the old line count for an append.
/// </param>
/// <param name="Deleted">Lines removed at that position.</param>
/// <param name="Added">Lines the commit put in their place.</param>
public sealed record LineEdit(int OldLine, int Deleted, int Added);

/// <summary>
///     Reads the edits out of the unified-diff text libgit2 renders for one file. Only the hunk headers
///     and the first character of each body line are looked at; the content itself is never needed,
///     because attribution is about positions.
/// </summary>
internal static class UnifiedDiff
{
    /// <summary>
    ///     The edits in <paramref name="patch" />, oldest position first. Empty for an empty patch, which
    ///     is what a pure rename or a mode change renders.
    /// </summary>
    public static IReadOnlyList<LineEdit> Edits(string patch)
    {
        var edits = new List<LineEdit>();
        // Everything before the first hunk is the file header, whose "---" and "+++" lines would read
        // as a delete and an add. Hunks start at a line beginning "@@".
        int position = 0;
        if (!patch.StartsWith("@@", StringComparison.Ordinal))
        {
            int first = patch.IndexOf("\n@@", StringComparison.Ordinal);
            if (first < 0) return edits;
            position = first + 1;
        }

        int oldLine = 0; // where in the old file the next body line sits
        int deleted = 0, added = 0; // the edit being accumulated, if any
        while (position < patch.Length)
        {
            int newline = patch.IndexOf('\n', position);
            if (newline < 0) newline = patch.Length;
            char kind = patch[position];
            switch (kind)
            {
                case '@':
                    Flush();
                    oldLine = HunkStart(patch.AsSpan(position, newline - position));
                    break;
                case '-':
                    deleted++;
                    break;
                case '+':
                    added++;
                    break;
                case ' ':
                    Flush();
                    oldLine++;
                    break;
                // "\ No newline at end of file" is a note about the line before it, not a line.
            }

            position = newline + 1;
        }

        Flush();
        return edits;

        void Flush()
        {
            if (deleted == 0 && added == 0) return;
            edits.Add(new LineEdit(oldLine, deleted, added));
            oldLine += deleted;
            deleted = 0;
            added = 0;
        }
    }

    /// <summary>
    ///     The 0-based old position a hunk's body starts at, from its <c>@@ -a[,b] +c[,d] @@</c> header.
    ///     <c>a</c> is 1-based, so the body starts at <c>a - 1</c> — except when <c>b</c> is 0: a hunk
    ///     that removes nothing names the line <em>before</em> the insertion, so its body starts at
    ///     <c>a</c>. The spike that produced this code got that wrong first and attributed every added
    ///     file to nothing; it is the one rule of the format worth a sentence.
    /// </summary>
    private static int HunkStart(ReadOnlySpan<char> header)
    {
        int minus = header.IndexOf('-') + 1;
        int end = minus;
        while (end < header.Length && char.IsAsciiDigit(header[end])) end++;
        int start = int.Parse(header[minus..end], System.Globalization.CultureInfo.InvariantCulture);
        int count = 1;
        if (end < header.Length && header[end] == ',')
        {
            int digits = ++end;
            while (end < header.Length && char.IsAsciiDigit(header[end])) end++;
            count = int.Parse(header[digits..end], System.Globalization.CultureInfo.InvariantCulture);
        }

        return count == 0 ? start : start - 1;
    }
}
