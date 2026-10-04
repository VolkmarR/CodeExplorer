namespace CodeExplorer.Search;

/// <summary>
///     <c>Search:TimeoutSeconds</c>, how long grep and <c>list_matches</c> may run a caller's pattern
///     before the search is stopped and refused (#373). Read here because the two searches stop under it
///     and must not drift apart into two settings. Only they get a limit: every other search builds its
///     pattern from a symbol or a path, and none of those can make RE2 scan for minutes.
/// </summary>
public static class SearchTimeout
{
    public const string Setting = "Search:TimeoutSeconds";

    /// <summary>
    ///     Twenty seconds. An ordinary search over a large project answers in well under one, and a
    ///     multiline count is bounded at about a second of reading (<see cref="GrepSearch.MaxMultilineCountMiB" />),
    ///     so twenty is far past anything a pattern worth running needs. A pattern RE2 can only run with
    ///     its NFA costs about half a minute for every 70 KB (#364), and holding a connection and a core
    ///     that long for a reply an agent cannot use is the cost this ends. Short enough that the agent,
    ///     told why, still has the time to narrow the pattern and ask again.
    /// </summary>
    public const int DefaultSeconds = 20;

    /// <summary>
    ///     The configured limit in seconds, or the default. A value that cannot be a limit is refused
    ///     rather than honoured: zero or less would refuse every search, and the upper bound keeps the
    ///     milliseconds inside what a timer takes.
    /// </summary>
    public static int Seconds(IConfiguration configuration)
    {
        int seconds = configuration.GetValue(Setting, DefaultSeconds);
        if (seconds is <= 0 or > int.MaxValue / 1000)
            throw new InvalidOperationException(
                $"{Setting} is {seconds}, but must be a number of seconds between 1 and {int.MaxValue / 1000}.");
        return seconds;
    }
}
