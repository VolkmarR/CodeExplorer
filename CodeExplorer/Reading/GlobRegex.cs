using System.Text;

namespace CodeExplorer.Reading;

/// <summary>
///     A glob as the RE2 expression that matches what SQL <c>GLOB</c> would (ADR-0004), so a caller's
///     pattern is run in linear time. DuckDB's <c>GLOB</c> recurses at every <c>*</c>, so a pattern with
///     k stars that cannot match costs about n^k steps per path of length n: twelve of them against a
///     sixty-character path held a query for minutes (GHSA-v284-9964-6mjr). RE2 never backtracks, so
///     the same pattern is one pass over the path.
///     The meaning is GLOB's: <c>*</c> is any run of characters and <c>?</c> any one, both crossing
///     <c>/</c>; a bracket that closes is a class, <c>!</c> first negating it and a <c>]</c> first (after
///     any <c>!</c>) a member; a range whose ends are reversed, such as <c>[z-a]</c>, is one no character
///     falls in, as GLOB compares it; everything else is literal. One difference, on purpose: <c>?</c> is
///     one character where GLOB takes one UTF-8 byte, so <c>?</c> no longer matches half an <c>ä</c>.
/// </summary>
public static class GlobRegex
{
    /// <summary>
    ///     <paramref name="glob" /> as RE2, unanchored: a caller matches it whole with
    ///     <c>regexp_full_match</c> or wraps it in <c>^…$</c>.
    /// </summary>
    /// <param name="glob">The pattern, in whatever case it is to be matched in.</param>
    /// <param name="widenDirectoryStars">
    ///     Lets a <c>/**/</c> between two segments also match no folder at all, which is what the overview's
    ///     excluded paths mean by it (<see cref="ExcludedPaths" />). As GLOB it could only have matched
    ///     <c>//</c> there, which no path holds, so it widens a glob and never narrows one.
    /// </param>
    public static string Translate(string glob, bool widenDirectoryStars = false)
    {
        var regex = new StringBuilder(glob.Length * 2);
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (widenDirectoryStars && c == '/' && StarsAfter(glob, i + 1) is var stars and > 0
                && i + 1 + stars < glob.Length && glob[i + 1 + stars] == '/')
            {
                regex.Append("/(?:.*/)?");
                i += stars + 1;
            }
            else if (c == '*')
            {
                // A run of stars means what one does, and is one `.*` rather than several: RE2 would
                // not backtrack over them either way, but the expression stays the size of the glob.
                regex.Append(".*");
                i += StarsAfter(glob, i + 1);
            }
            else if (c == '?')
                regex.Append('.');
            else if (c == '[' && ClassEnd(glob, i) is var end and > 0)
            {
                Class(regex, glob[(i + 1)..end]);
                i = end;
            }
            else
                regex.Append(Escaped(c, false));
        }

        return regex.ToString();
    }

    /// <summary>
    ///     The first range in <paramref name="glob" /> whose ends are reversed, such as <c>z-a</c>, or null.
    ///     GLOB accepts one and matches nothing with it, so a surface that must not answer malformed input
    ///     with an empty result refuses it by this.
    /// </summary>
    public static string? ReversedRange(string glob)
    {
        for (int i = 0; i < glob.Length; i++)
        {
            if (glob[i] != '[' || ClassEnd(glob, i) is not (var end and > 0)) continue;
            string members = glob[(i + 1)..end];
            if (members.StartsWith('!')) members = members[1..];
            for (int j = 0; j + 2 < members.Length; j++)
                if (members[j + 1] == '-')
                {
                    if (members[j] > members[j + 2]) return members.Substring(j, 3);
                    j += 2;
                }

            i = end;
        }

        return null;
    }

    /// <summary>
    ///     One bracket's members as an RE2 class, member by member rather than copied, so that nothing in
    ///     it can be read by RE2 as its own syntax (<c>[:alpha:]</c>) or refused by it (a reversed range).
    /// </summary>
    private static void Class(StringBuilder regex, string members)
    {
        bool negated = members.StartsWith('!');
        if (negated) members = members[1..];

        var set = new StringBuilder();
        for (int j = 0; j < members.Length; j++)
        {
            if (j + 2 < members.Length && members[j + 1] == '-')
            {
                // Reversed, no character is within it, which is how GLOB compares one: it adds nothing.
                if (members[j] <= members[j + 2])
                    set.Append(Escaped(members[j], true)).Append('-').Append(Escaped(members[j + 2], true));
                j += 2;
            }
            else
                set.Append(Escaped(members[j], true));
        }

        // A class left with no members matches no character, or every one when negated. RE2 has no
        // empty class to write, so each is spelled as the set it is.
        if (set.Length == 0) regex.Append(negated ? @"[\x{0}-\x{10FFFF}]" : @"[^\x{0}-\x{10FFFF}]");
        else regex.Append(negated ? "[^" : "[").Append(set).Append(']');
    }

    /// <summary>How many stars run from <paramref name="start" />, so a run is read as the one it means.</summary>
    private static int StarsAfter(string glob, int start)
    {
        int end = start;
        while (end < glob.Length && glob[end] == '*') end++;
        return end - start;
    }

    /// <summary>
    ///     Where the class opened at <paramref name="open" /> closes, or 0 where it never does and the
    ///     bracket is a literal. A <c>]</c> first in the class, after any <c>!</c>, is a member, as GLOB
    ///     reads it.
    /// </summary>
    private static int ClassEnd(string glob, int open)
    {
        int start = open + 1;
        if (start < glob.Length && glob[start] == '!') start++;
        if (start < glob.Length && glob[start] == ']') start++;
        int end = glob.IndexOf(']', start);
        return end < 0 ? 0 : end;
    }

    /// <summary>
    ///     A character made literal for RE2, which refuses an escaped letter or space, so only its own
    ///     metacharacters are escaped. Inside a class, the ones that mean something there.
    /// </summary>
    private static string Escaped(char c, bool inClass) =>
        (inClass ? @"\]-[^" : @"\.+*?()|[]{}^$").Contains(c, StringComparison.Ordinal) ? $"\\{c}" : c.ToString();
}
