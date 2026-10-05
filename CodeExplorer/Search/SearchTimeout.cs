namespace CodeExplorer.Search;

/// <summary>
///     <c>Search:TimeoutSeconds</c>, how long grep and <c>list_matches</c> may run a caller's pattern
///     before the search is stopped and refused (#373). Read here because the two searches stop under it
///     and must not drift apart into two settings. Only they get a limit, and grep only for a pattern:
///     every other search builds its pattern from a symbol or a path, and a text query hands RE2 nothing,
///     so none of those can make RE2 scan for minutes.
///     The limit cancels a token, and DuckDB acts on it at its next check between chunks of work, so
///     it bounds how long a search goes on rather than ending it on the second. Two shapes overrun it
///     by much more: a line scan whose pattern matches nothing reads on to the end of a row group
///     before it checks (about 123,000 lines), and a multiline search matches a whole chunk of
///     documents before it checks, so it runs on for about half of what finishing would have cost.
///     Measured, over two thousand 5 KB files, 25 s past the limit for a search that took 50 s to
///     finish, and over two hundred, 2.3 s past the limit for one that took 7 s. Nor can it stop inside
///     one file's match, so a single very large file can hold it for longer still.
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
    ///     The configured limit in seconds, or the default. Zero or less is refused rather than honoured:
    ///     it would refuse every pattern search.
    /// </summary>
    public static int Seconds(IConfiguration configuration) =>
        Infrastructure.Setting.Seconds(configuration, Setting, DefaultSeconds);
}
