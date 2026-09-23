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
        foreach ((string needle, string name) in _unsupportedSyntax)
            if (pattern.Contains(needle, StringComparison.Ordinal))
                return $"The pattern uses {name}, which RE2 does not support. "
                       + "Match the wider text instead and read the hit, or grep for the inner part with context.";

        // \1..\9 is a backreference; \0 is not one and \\1 is an escaped backslash followed by a digit.
        for (int i = 0; i + 1 < pattern.Length; i++)
        {
            if (pattern[i] != '\\') continue;
            if (pattern[i + 1] is >= '1' and <= '9')
                return $"The pattern uses a backreference (\\{pattern[i + 1]}), which RE2 does not support. "
                       + "Repeat the text instead of referring back to a group.";
            i++;
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
        + "RE2 has no lookaround and no backreferences; escape literal metacharacters with a backslash.";

    /// <summary>
    ///     How many capture groups the pattern opens, counting only real ones: <c>(?:</c>, <c>(?i)</c>
    ///     and friends capture nothing, a <c>\(</c> is a literal parenthesis and a <c>(</c> inside a
    ///     character class is one too. Used to tell "group 1 matched nothing" from "there is no group
    ///     1", which read identically as an empty answer and mean opposite things.
    /// </summary>
    public static int CaptureGroups(string pattern)
    {
        int groups = 0;
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (inClass)
            {
                if (c == ']') inClass = false;
                continue;
            }

            if (c == '[') inClass = true;
            else if (c == '(' && (i + 1 >= pattern.Length || pattern[i + 1] != '?')) groups++;
        }

        return groups;
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }
}
