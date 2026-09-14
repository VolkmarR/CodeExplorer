namespace CodeExplorer;

/// <summary>
///     Reading a configured URL, which three settings now need and which each of them used to do its
///     own way. Absent selects a local default everywhere here (ADR-0004), so absent is an answer and
///     not a failure; what is worth spelling out once is the other case, where a value is present and
///     unusable.
/// </summary>
public static class Setting
{
    /// <summary>
    ///     The setting as an absolute URL, or null when it is absent. A value that is not a URL names
    ///     the setting rather than crashing with <c>UriFormatException</c>: the server refuses to start
    ///     over this, and "Invalid URI: The format of the URI could not be determined" does not say
    ///     which of them was wrong. <see cref="InvalidOperationException" /> and not
    ///     <c>McpException</c>: no MCP tool is on this path, startup is.
    /// </summary>
    /// <param name="configuration">Where the setting is read from.</param>
    /// <param name="key">The setting's name, which is what a failure has to say.</param>
    /// <param name="remedy">What to do instead, in a sentence, since what absent selects differs per setting.</param>
    public static Uri? Url(IConfiguration configuration, string key, string remedy)
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
            throw new InvalidOperationException($"{key} is '{value}', which is not an absolute URL. {remedy}");
        return url;
    }
}
