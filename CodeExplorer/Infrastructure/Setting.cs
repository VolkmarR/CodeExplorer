namespace CodeExplorer.Infrastructure;

/// <summary>
///     Reading the settings more than one component reads: a configured URL, which three settings need
///     and which each of them used to do its own way, and the data directory, which five components
///     placed themselves under with their own copy of its name and default. Absent selects a local
///     default everywhere here (ADR-0004), so absent is an answer and not a failure; what is worth
///     spelling out once is the other case, where a value is present and unusable.
/// </summary>
public static class Setting
{
    /// <summary>The setting that moves everything this server keeps on disk.</summary>
    public const string DataDirectoryKey = "Storage:DataDirectory";

    /// <summary>
    ///     The directory the control database, the indexes, the local copies, the local durable store
    ///     and the scratch space live under. Absent, it is <c>data</c> beside the working directory, so
    ///     a plain <c>dotnet run</c> needs no setting. The stdio proxy keeps its own copy of the name,
    ///     because it references no project of the server (CODING_STANDARDS, Layout).
    /// </summary>
    public static string DataDirectory(IConfiguration configuration) => configuration[DataDirectoryKey] ?? "data";

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
