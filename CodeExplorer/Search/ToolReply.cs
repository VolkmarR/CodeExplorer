using System.Globalization;

namespace CodeExplorer;

/// <summary>
///     The reply shaping every index-backed tool shares: the size ceiling, the line clip and the
///     wording for a project that has nothing to answer from. One place, so `grep` and `read_file`
///     cannot drift into telling an agent two different limits.
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

    /// <summary>The one explanation every index-backed tool gives when there is nothing to read from.</summary>
    public static string NoIndex(string slug) =>
        $"Project '{slug}' has no index to read from right now: it was never built, or a refresh is still building the first one. "
        + $"Ask the operator to refresh it with POST /api/projects/{slug}/refresh, or retry shortly.";

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

    public static string Bytes(long size) => size switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{size} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{size / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{size / (1024.0 * 1024.0):0.#} MB")
    };
}
