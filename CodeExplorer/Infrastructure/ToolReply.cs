using System.Globalization;

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
