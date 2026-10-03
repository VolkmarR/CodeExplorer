using System.Text;

namespace CodeExplorer.Search;

/// <summary>
///     Turning the document a multiline search marked into the lines a reply shows: the marks the
///     whole-word rewrite doubled taken out again, and each match cut into its lines with the context
///     around it. Apart from the read because it runs on text the engine already returned and touches
///     no connection.
/// </summary>
public sealed partial class GrepSearch
{
    /// <summary>
    ///     Content marked from <see cref="CodeExplorer.Language.SymbolText.WholeWordMatches" /> back to
    ///     content marked the plain way. Every match was rewritten as START skipped END, START match END,
    ///     and then the match whole, which begins with the skipped text and the match again: the skipped
    ///     text is kept in front of the marked match, the repeat of both is dropped, and so is the empty
    ///     pair in front of the rest of the document.
    /// </summary>
    private static string WithoutMarkedCopies(string marked)
    {
        var text = new StringBuilder(marked.Length);
        for (int i = 0; i < marked.Length; i++)
        {
            int skippedEnd, matchEnd;
            if (marked[i] != _matchStart
                || (skippedEnd = marked.IndexOf(_matchEnd, i + 1)) < 0
                || skippedEnd + 1 >= marked.Length || marked[skippedEnd + 1] != _matchStart
                || (matchEnd = marked.IndexOf(_matchEnd, skippedEnd + 2)) < 0)
            {
                text.Append(marked[i]);
                continue;
            }

            int skipped = skippedEnd - i - 1;
            int match = matchEnd - skippedEnd - 2;
            text.Append(marked, i + 1, skipped);
            if (match > 0) text.Append(marked, skippedEnd + 1, match + 2);
            i = matchEnd + skipped + match;
        }

        return text.ToString();
    }

    /// <summary>
    ///     Walks content in which every match is wrapped in <see cref="_matchStart" /> and
    ///     <see cref="_matchEnd" />, marks every line a match spans, adds the context window, and returns
    ///     the lines with how many matches they cover. Empty matches are skipped: they span nothing.
    ///     The walk records only where each line starts; text is cut out, markers removed, for the
    ///     shown lines alone, because a file on the page can be thousands of lines around a few matches.
    /// </summary>
    private static (List<GrepLine> Lines, int MatchesShown) SpannedLines(string marked, Bounds bounds)
    {
        var lineStarts = new List<int> { 0 };
        var matched = new HashSet<int>();
        var shown = new SortedSet<int>();
        int matchesSeen = 0, matchesShown = 0, matchStartLine = 0, matchStartIndex = 0;
        for (int index = 0; index < marked.Length; index++)
            switch (marked[index])
            {
                case _matchStart:
                    matchStartLine = lineStarts.Count;
                    matchStartIndex = index;
                    break;
                case _matchEnd:
                    // Nothing between the markers, not even a newline: an empty match.
                    if (index == matchStartIndex + 1) break;
                    int line = lineStarts.Count;
                    if (matchesSeen < bounds.MaxLinesPerFile && shown.Count < MaxMultilineLinesShown)
                    {
                        // A match may span the whole file — `(?s).*` does — so the lines it marks are
                        // capped as well as the matches (GHSA-v284-9964-6mjr).
                        for (int i = matchStartLine; i <= line && matched.Count < MaxMultilineLinesShown; i++) matched.Add(i);
                        for (int i = Math.Max(1, matchStartLine - bounds.Context);
                             i <= line + bounds.Context && shown.Count < MaxMultilineLinesShown; i++)
                            shown.Add(i);
                        matchesShown++;
                    }

                    matchesSeen++;
                    break;
                case '\n':
                    lineStarts.Add(index + 1);
                    break;
            }

        return (
            [.. shown.Where(i => i <= lineStarts.Count).Select(i => new GrepLine(i, LineText(i), matched.Contains(i)))],
            matchesShown);

        string LineText(int line)
        {
            int start = lineStarts[line - 1];
            int end = line < lineStarts.Count ? lineStarts[line] - 1 : marked.Length;
            var span = marked.AsSpan(start, end - start);
            if (span.IndexOfAny(_matchStart, _matchEnd) < 0) return new string(span);

            var text = new StringBuilder(span.Length);
            foreach (char c in span)
                if (c is not (_matchStart or _matchEnd))
                    text.Append(c);
            return text.ToString();
        }
    }
}
