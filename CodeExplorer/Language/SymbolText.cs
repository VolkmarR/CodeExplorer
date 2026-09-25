using System.Buffers;
using System.Text;

namespace CodeExplorer.Language;

/// <summary>
///     Reading an identifier out of a line, and writing one into a pattern. Where a word ends, how
///     far a line is indented and how a literal is spelled for RE2 are the same in every language
///     this indexes today, so they sit beside the language seam rather than inside a profile — a
///     second copy of any of them in a caller is how two answers about one line start to disagree.
///     They live in <c>Language/</c> and not in <c>Search/</c> because what counts as a word is a
///     language fact in waiting: <c>$</c> is one in JavaScript and <c>-</c> is one in CSS, and when
///     that matters the answer will come from a profile.
/// </summary>
public static class SymbolText
{
    /// <summary>
    ///     The characters RE2 and .NET both read as pattern syntax. Not <see cref="System.Text.RegularExpressions.Regex.Escape" />,
    ///     which also escapes whitespace and <c>#</c> in ways RE2 rejects — and a literal here may be
    ///     a phrase with a space in it, which would fail inside DuckDB rather than at the call site.
    /// </summary>
    private static readonly SearchValues<char> _metacharacters = SearchValues.Create(@"\.+*?()|[]{}^$");

    /// <summary>
    ///     This text as an RE2 pattern matching it literally. One copy, because both halves of a
    ///     reference search hand a pattern built this way to the same <c>regexp_matches</c>: the
    ///     symbol the caller asked for, and the declaration shapes a language declares.
    ///     An identifier usually has nothing to escape, and then it is its own pattern.
    /// </summary>
    public static string Re2Literal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int first = text.AsSpan().IndexOfAny(_metacharacters);
        if (first < 0) return text;

        var pattern = new StringBuilder(text.Length + 8).Append(text, 0, first);
        foreach (char c in text.AsSpan(first))
        {
            if (_metacharacters.Contains(c)) pattern.Append('\\');
            pattern.Append(c);
        }

        return pattern.ToString();
    }

    /// <summary>
    ///     <see cref="IsWordChar" /> as an RE2 character class, which .NET reads the same way. Spelled out
    ///     because RE2's <c>\w</c> and <c>\b</c> are ASCII-only: <c>\bÄnderung</c> found nothing after a
    ///     space and <c>\bbar</c> found <c>fooÄbar</c>, so an identifier with a German or Italian letter
    ///     at either end was a symbol "nothing in this project spells" (#235).
    /// </summary>
    public const string Re2WordChar = $"[{Re2WordClass}]";

    /// <summary>Any character <see cref="Re2WordChar" /> is not, line breaks included.</summary>
    public const string Re2NonWordChar = $"[^{Re2WordClass}]";

    /// <summary>The inside of <see cref="Re2WordChar" />, for a class that holds a word character and more.</summary>
    public const string Re2WordClass = @"\p{L}\p{Nd}_";

    /// <summary>
    ///     A word boundary in front of a match, for RE2, which has no lookbehind to test one without
    ///     consuming the character before it. That is harmless to a yes-or-no test of a line; a caller
    ///     that counts or extracts matches has to allow for it, because two matches one character apart
    ///     share the character between them.
    /// </summary>
    public const string Re2WordStart = $"(?:^|{Re2NonWordChar})";

    /// <summary>A word boundary after a match; consumes the character after it, as <see cref="Re2WordStart" /> does.</summary>
    public const string Re2WordEnd = $"(?:$|{Re2NonWordChar})";

    /// <summary>
    ///     The identifier as an RE2 pattern for the engine: escaped, and anchored on a word boundary at
    ///     each end that has a word character to anchor to. A boundary against punctuation would mean
    ///     the opposite of what it does against a letter, so it is left off there rather than applied
    ///     blindly.
    ///     One copy, because the two searches that ask the engine for a symbol anchor it here and then
    ///     re-find it on the line with <see cref="IndexOf" />: two definitions of a word character
    ///     would be the two matchers disagreeing that this design exists to prevent.
    ///     It tests a line and does not count on it; <see cref="OccurrencePattern" /> is the one that counts.
    /// </summary>
    public static string WholeWordPattern(string symbol) => Bounded(symbol, Re2WordEnd, "");

    /// <summary>
    ///     The symbol escaped, behind a start boundary where it starts with a word character and in
    ///     front of <paramref name="wordEnd" /> or <paramref name="otherEnd" /> by how it ends.
    /// </summary>
    private static string Bounded(string symbol, string wordEnd, string otherEnd)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Length == 0) return otherEnd;
        string head = IsWordChar(symbol[0]) ? Re2WordStart : "";
        string tail = IsWordChar(symbol[^1]) ? wordEnd : otherEnd;
        return head + Re2Literal(symbol) + tail;
    }

    /// <summary>
    ///     The identifier as an RE2 pattern whose every match is a candidate occurrence, and whose group
    ///     1 is empty exactly when that candidate is a whole one. <see cref="WholeWordPattern" /> cannot
    ///     count: its end boundary consumes the character after a match, so in <c>Ship(Ship())</c> the
    ///     second name had lost the <c>(</c> its own start boundary needed. Here the end is not a
    ///     boundary but a capture of the word character that would break one — never a character the
    ///     next match can start on, since a start boundary is not a word character.
    /// </summary>
    public static string OccurrencePattern(string symbol) => Bounded(symbol, $"({Re2WordChar}?)", "()");

    /// <summary>
    ///     Where the identifier next sits on the line, on word boundaries, or -1. Done by hand rather
    ///     than with a per-call <c>\b…\b</c> regex because the symbol is caller text: escaping it into
    ///     a pattern to find something that is not a pattern buys nothing and can only go wrong.
    /// </summary>
    public static int IndexOf(string line, string symbol, int from = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (from > line.Length) return -1;
        int at = IndexOfWord(line.AsSpan(from), symbol, StringComparison.Ordinal);
        return at < 0 ? -1 : at + from;
    }

    /// <summary>
    ///     Whether the word appears whole somewhere in the text. The comparison is the caller's,
    ///     because a keyword is case-insensitive in X#, Delphi and SQL and is not in C#: asking
    ///     ordinally on a language that shouts its keywords answers "no declaration here" for every
    ///     <c>CREATE PROCEDURE</c> in the project.
    /// </summary>
    public static bool ContainsWord(ReadOnlySpan<char> text, string word, StringComparison comparison) =>
        IndexOfWord(text, word, comparison) >= 0;

    /// <summary>Whether this character can sit inside an identifier.</summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    ///     Where the word first sits whole in the text, or -1. A span, because a caller holding one
    ///     side of a line must not copy it to ask: the lines here are whatever the index holds, and
    ///     one of them can be a whole minified bundle.
    ///     A boundary is only required at an end the word itself has a word character at — a <c>\b</c>
    ///     against punctuation means the opposite of what it means against a letter.
    /// </summary>
    private static int IndexOfWord(ReadOnlySpan<char> text, string word, StringComparison comparison)
    {
        if (word.Length == 0) return -1;
        for (int i = 0; i < text.Length;)
        {
            int next = text[i..].IndexOf(word, comparison);
            if (next < 0) return -1;
            i += next;

            bool startsClean = !IsWordChar(word[0]) || i == 0 || !IsWordChar(text[i - 1]);
            int after = i + word.Length;
            bool endsClean = !IsWordChar(word[^1]) || after >= text.Length || !IsWordChar(text[after]);
            if (startsClean && endsClean) return i;
            i++;
        }

        return -1;
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
}
