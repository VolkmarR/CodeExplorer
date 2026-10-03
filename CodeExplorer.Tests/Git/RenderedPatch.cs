using CodeExplorer.Git;

namespace CodeExplorer.Tests;

/// <summary>
///     Reads the edits out of the unified-diff text libgit2 renders for one file. Only the hunk headers
///     and the first character of each body line are looked at; the content itself is never needed,
///     because attribution is about positions.
///     The history walk no longer renders text: <see cref="NativeDiff" /> reads the same edits off
///     libgit2's hunk structs (#292), placing each by <see cref="UnifiedDiff.OldPosition" />. This reading
///     of the rendered patch is the independent account the tests hold that one to, so it lives with them.
/// </summary>
internal static class RenderedPatch
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
    ///     The 0-based old position a hunk's body starts at, from its <c>@@ -a[,b] +c[,d] @@</c> header,
    ///     by the rule <see cref="UnifiedDiff.OldPosition" /> gives.
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

        return UnifiedDiff.OldPosition(start, count);
    }
}
