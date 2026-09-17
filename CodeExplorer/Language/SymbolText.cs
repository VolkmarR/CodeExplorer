namespace CodeExplorer;

/// <summary>
///     Reading identifiers out of a line, in the ways that hold in every language this indexes. Where
///     a word ends and how far a line is indented are not language facts, so they sit beside the
///     language seam rather than inside a profile — a second copy of either in a caller is how two
///     answers about one line start to disagree.
/// </summary>
public static class SymbolText
{
    /// <summary>
    ///     Where <paramref name="symbol" /> sits on this line, on word boundaries, every time it does.
    ///     Every one is classified and not only the first: <c>return Foo.Create(Foo.Default)</c> is a
    ///     type use and a read, and reporting it as one of them loses the other.
    /// </summary>
    public static IEnumerable<int> Occurrences(string line, string symbol)
    {
        if (symbol.Length == 0) yield break;
        for (int i = IndexOf(line, symbol); i >= 0; i = IndexOf(line, symbol, i + 1))
            yield return i;
    }

    /// <summary>
    ///     Where the identifier next sits on the line, on word boundaries, or -1. Done by hand rather
    ///     than with a per-call <c>\b…\b</c> regex because the symbol is caller text: escaping it into
    ///     a pattern to find something that is not a pattern buys nothing and can only go wrong.
    /// </summary>
    public static int IndexOf(string line, string symbol, int from = 0,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (symbol.Length == 0 || from > line.Length) return -1;
        for (int i = line.IndexOf(symbol, from, comparison);
             i >= 0;
             i = line.IndexOf(symbol, i + 1, comparison))
        {
            bool startsClean = !IsWord(symbol[0]) || i == 0 || !IsWord(line[i - 1]);
            int after = i + symbol.Length;
            bool endsClean = !IsWord(symbol[^1]) || after >= line.Length || !IsWord(line[after]);
            if (startsClean && endsClean) return i;
        }

        return -1;
    }

    /// <summary>
    ///     Whether the word appears whole somewhere in the text. The comparison is the caller's,
    ///     because a keyword is case-insensitive in X#, Delphi and SQL and is not in C#: asking
    ///     ordinally on a language that shouts its keywords answers "no declaration here" for every
    ///     <c>CREATE PROCEDURE</c> in the project.
    /// </summary>
    public static bool ContainsWord(ReadOnlySpan<char> text, string word, StringComparison comparison)
    {
        // A span and not a string, because the caller holds one side of a line it must not copy to
        // ask this: the lines here are whatever the index holds, and one of them can be a whole
        // minified bundle.
        for (int i = text.IndexOf(word, comparison); i >= 0;)
        {
            bool startsClean = i == 0 || !IsWord(text[i - 1]);
            int after = i + word.Length;
            bool endsClean = after >= text.Length || !IsWord(text[after]);
            if (startsClean && endsClean) return true;

            int next = text[(i + 1)..].IndexOf(word, comparison);
            if (next < 0) return false;
            i += 1 + next;
        }

        return false;
    }

    /// <summary>Columns of leading whitespace, a tab counted as four — the width most source is written to.</summary>
    public static int Indent(string line)
    {
        int columns = 0;
        foreach (char c in line)
            if (c == ' ') columns++;
            else if (c == '\t') columns += 4;
            else break;
        return columns;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
