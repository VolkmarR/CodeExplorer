using CodeExplorer.Language;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>
///     What every tool that hands a caller's pattern to DuckDB has to say about RE2 (ADR-0004): which
///     constructs it does not have, and what one of its rejections means. Shared so that <c>grep</c>
///     and <c>list_matches</c> cannot explain the same refused pattern two different ways — an agent
///     that is told lookbehind is unsupported by one tool and handed a parser error by another will
///     retry the same pattern on the other tool.
/// </summary>
internal static class Re2
{
    /// <summary>
    ///     RE2 rejects these; each is a .NET or PCRE habit an agent brings along. Recognised up front so
    ///     the explanation names the construct rather than quoting an engine error.
    /// </summary>
    private static readonly (string Needle, string Name)[] _unsupportedSyntax =
    [
        ("(?<=", "lookbehind (?<=...)"),
        ("(?<!", "negative lookbehind (?<!...)"),
        ("(?=", "lookahead (?=...)"),
        ("(?!", "negative lookahead (?!...)")
    ];

    /// <summary>Why this pattern cannot be run at all, or null when RE2 may have a go at it.</summary>
    public static string? Unsupported(string pattern)
    {
        foreach (int open in GroupOpenings(pattern))
        {
            var group = pattern.AsSpan(open);
            foreach ((string needle, string name) in _unsupportedSyntax)
                if (group.StartsWith(needle, StringComparison.Ordinal))
                    return $"The pattern uses {name}, which RE2 does not support. "
                           + "Match the wider text instead and read the hit, or grep for the inner part with context.";
        }

        // \1..\9 is a backreference; \0 is not one, and neither is \\1, an escaped backslash followed by
        // a digit, or a \1 quoted by \Q...\E.
        for (int i = 0; i + 1 < pattern.Length; i++)
        {
            if (pattern[i] != '\\') continue;
            if (pattern[i + 1] is >= '1' and <= '9')
                return $"The pattern uses a backreference (\\{pattern[i + 1]}), which RE2 does not support. "
                       + "Repeat the text instead of referring back to a group.";
            i = EscapeEnd(pattern, i);
        }

        return null;
    }

    /// <summary>
    ///     True when a DuckDB failure is RE2 refusing the caller's pattern rather than the index being
    ///     broken. Only a pattern hands user text to a parser, and RE2 rejections surface as DuckDB's
    ///     "Invalid Input Error: missing ): ..." with no other marker. A missing table or a detached
    ///     catalog is a Catalog or Binder error and belongs to infrastructure, which throws.
    /// </summary>
    public static bool IsPatternRejection(DuckDBException exception) =>
        exception.Message.StartsWith("Invalid Input Error", StringComparison.Ordinal);

    /// <summary>The rejection as agent-facing prose, naming what RE2 lacks rather than only quoting it.</summary>
    public static string Rejected(DuckDBException exception) =>
        $"The pattern is not a valid RE2 regular expression: {FirstLine(exception.Message)}. "
        + "RE2 has no lookaround and no backreferences; escape literal metacharacters with a backslash."
        // The .NET spelling (?<name>...) arrived in RE2 after the version DuckDB bundles, whose refusal
        // ("invalid perl operator: (?<") does not say what to write instead (#238). Keyed on the
        // engine's own message, so an upgrade that accepts the spelling stops the advice by itself.
        + (exception.Message.Contains("invalid perl operator: (?<", StringComparison.Ordinal)
            ? " Name a group as (?P<name>...)."
            : "");

    /// <summary>
    ///     The caller's pattern as a whole word: no letter, digit or underscore immediately before or
    ///     after the match, the way <c>grep -w</c> reads it and <see cref="CodeExplorer.Language.SymbolText.IsWordChar" />
    ///     defines one. Not <c>\b</c>, which RE2 reads as ASCII: it found <c>bar</c> inside
    ///     <c>fooÄbar</c> and nothing in front of <c>Ändern</c> (#235). The boundaries consume a
    ///     character, so this tests a line and must not count on one; <see cref="WholeWordTokens" />
    ///     counts.
    /// </summary>
    public static string WholeWord(string pattern) =>
        $"{SymbolText.Re2WordStart}(?:{pattern}){SymbolText.Re2WordEnd}";

    /// <summary>
    ///     Compiles the caller's pattern on its own, throwing the <see cref="DuckDBException" /> that
    ///     <see cref="IsPatternRejection" /> recognises when RE2 refuses it. Run before any wrapped form
    ///     is, because a wrapper can balance what the caller left unbalanced: <c>a)|(b</c> is no pattern,
    ///     yet <c>(?:a)|(b)</c> is one that matches something else and captures a group besides (#238).
    ///     An empty subject, so the check costs a compile and no scan.
    /// </summary>
    public static async Task CompileAsync(DuckDBConnection connection, string pattern,
        CancellationToken cancellationToken)
    {
        await using var command = connection.Query("SELECT regexp_matches('', $pattern)",
            [new DuckDBParameter("pattern", pattern)]);
        await command.ScalarAsync(cancellationToken);
    }

    /// <summary>
    ///     The caller's pattern as a whole word, written so that every character of the text belongs to
    ///     exactly one match: either the caller's pattern and the boundary after it, with group 1 the
    ///     caller's match and group <c>n + 1</c> the caller's group <c>n</c>; or a stretch between
    ///     matches, with group 1 empty.
    ///     <see cref="WholeWord" /> alone cannot extract. Its end boundary consumes the character after
    ///     a match, so in <c>bar,bar</c> the second had lost the comma its start boundary needed, and a
    ///     line break shared the same way lost the match on the next line. Here a start boundary is never
    ///     tested: a word is eaten whole together with the character after it, a character that is not a
    ///     word character is eaten alone, and so the caller's pattern is only ever tried where a word
    ///     could start — which is what a lookbehind would have said, in the engine that has none.
    /// </summary>
    public static string WholeWordTokens(string pattern) =>
        $"({pattern}){SymbolText.Re2WordEnd}|{SymbolText.Re2WordChar}+{SymbolText.Re2NonWordChar}?|{SymbolText.Re2NonWordChar}";

    /// <summary>
    ///     The pattern with a <c>\Q</c> it leaves open closed by <c>\E</c>, which RE2 reads the same:
    ///     alone, it quotes to the end of the pattern. Applied once where a caller's pattern comes in,
    ///     so that no wrapper put around it later can have its closing parenthesis quoted too, which
    ///     turned a pattern RE2 accepted into one it refuses (#238).
    /// </summary>
    public static string WithQuoteClosed(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '\\') continue;
            int end = EscapeEnd(pattern, i);
            if (pattern.AsSpan(i).StartsWith("\\Q") && !pattern.AsSpan(i + 2, end - i - 1).EndsWith("\\E"))
                return pattern + "\\E";
            i = end;
        }

        return pattern;
    }

    /// <summary>
    ///     How many capture groups the pattern opens, counting only real ones: a plain <c>(</c> and a
    ///     named <c>(?P&lt;name&gt;</c> capture; <c>(?:</c>, <c>(?i)</c> and friends
    ///     do not, and a parenthesis that is escaped, quoted by <c>\Q...\E</c> or inside a character class
    ///     is a literal. Used to tell "group 1 matched nothing" from "there is no group 1", which read
    ///     identically as an empty answer and mean opposite things.
    /// </summary>
    public static int CaptureGroups(string pattern) =>
        GroupOpenings(pattern).Count(open =>
            !pattern.AsSpan(open).StartsWith("(?") || pattern.AsSpan(open).StartsWith("(?P<"));

    /// <summary>
    ///     Whether a flag group in the pattern (<c>(?i)</c>, <c>(?mi:</c>) may turn case folding on. Any
    ///     <c>i</c> among a group's flags counts, even after a <c>-</c> that turns it off. A caller that
    ///     narrows on the answer gets only a weaker filter from a wrong yes, but a wrong no would drop
    ///     a match.
    /// </summary>
    public static bool MayFoldCase(string pattern) =>
        GroupOpenings(pattern).Any(open =>
        {
            if (!pattern.AsSpan(open).StartsWith("(?")) return false;
            for (int i = open + 2; i < pattern.Length && (char.IsAsciiLetter(pattern[i]) || pattern[i] == '-'); i++)
                if (pattern[i] == 'i') return true;
            return false;
        });

    /// <summary>
    ///     The index of every <c>(</c> that opens a group, skipping the ones that are literals: escaped,
    ///     quoted by <c>\Q...\E</c> or inside a character class. A needle searched for in the raw text
    ///     would find <c>(?=</c> in <c>\(?=</c>, an optional literal parenthesis before an equals sign.
    /// </summary>
    private static IEnumerable<int> GroupOpenings(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i = EscapeEnd(pattern, i);
                    break;
                case '[':
                    // An unterminated class is a pattern RE2 rejects; nothing after it is a group.
                    i = ClassEnd(pattern, i);
                    if (i < 0) yield break;
                    break;
                case '(':
                    yield return i;
                    break;
            }
        }
    }

    /// <summary>
    ///     The index of the last character of the escape starting at <paramref name="start" />, in RE2's
    ///     forms: <c>\xHH</c>, <c>\x{...}</c>, <c>\pX</c>, <c>\p{...}</c>, <c>\Q...\E</c>, up to three octal
    ///     digits, or a single character. A malformed tail runs to the end, which RE2 rejects anyway.
    /// </summary>
    public static int EscapeEnd(string pattern, int start)
    {
        int last = pattern.Length - 1;
        int i = start + 1;
        if (i > last) return last;
        switch (pattern[i])
        {
            case 'x' or 'p' or 'P' when i < last && pattern[i + 1] == '{':
                int close = pattern.IndexOf('}', i);
                return close < 0 ? last : close;
            case 'x':
                return Math.Min(i + 2, last);
            case 'p' or 'P':
                return Math.Min(i + 1, last);
            case 'Q':
                int end = pattern.IndexOf("\\E", i, StringComparison.Ordinal);
                return end < 0 ? last : end + 1;
            case >= '0' and <= '7':
                while (i < last && i - start < 3 && pattern[i + 1] is >= '0' and <= '7') i++;
                return i;
            default:
                return i;
        }
    }

    /// <summary>
    ///     The index of the <c>]</c> closing the class opened at <paramref name="start" />, or -1. A
    ///     <c>]</c> right after <c>[</c> or <c>[^</c> is a member, as are escapes and <c>[:name:]</c>.
    /// </summary>
    public static int ClassEnd(string pattern, int start)
    {
        int i = start + 1;
        if (i < pattern.Length && pattern[i] == '^') i++;
        if (i < pattern.Length && pattern[i] == ']') i++;
        while (i < pattern.Length)
        {
            switch (pattern[i])
            {
                case ']':
                    return i;
                case '\\':
                    i = EscapeEnd(pattern, i);
                    break;
                case '[' when i + 1 < pattern.Length && pattern[i + 1] == ':':
                    int close = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);
                    if (close >= 0) i = close + 1;
                    break;
            }

            i++;
        }

        return -1;
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }
}
