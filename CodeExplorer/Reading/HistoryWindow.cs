using System.Globalization;

namespace CodeExplorer;

/// <summary>
///     A span of history to read over: everything authored between <see cref="Since" /> and
///     <see cref="Until" />, inclusive. Every question asked of history that is not about one commit
///     or one file is asked over one of these, so the way a window is expressed and applied is
///     decided once rather than per tool.
///     A window is anchored to the history that was imported and never to the clock. An index is
///     built by a refresh and may be days behind its remotes (CONTEXT.md, History), so "the last 30
///     days" measured from now would hand back an empty ranking for a project nobody refreshed this
///     month — an answer an agent reads as "nothing changed", which is the one thing history must
///     never say by accident. Measured back from the newest recorded commit instead, the same call
///     answers with the last 30 days there were, and says which dates those are.
/// </summary>
/// <param name="Since">The oldest authored date in the window.</param>
/// <param name="Until">The newest, which is the newest commit the window was anchored to.</param>
/// <param name="Days">
///     How many days the window was asked for, so a reply can say which question it is answering.
///     Redundant with the two dates by construction, and kept because it is the number the caller
///     passed: a reader comparing "ninety days" against a window that ends in July learns something
///     the subtraction would not have told them.
/// </param>
public sealed record HistoryWindow(DateTimeOffset Since, DateTimeOffset Until, int Days)
{
    /// <summary>
    ///     A quarter. Long enough that an ordinary sprint's churn does not read as one file, short
    ///     enough that a year of a rewrite does not drown what moved this month.
    /// </summary>
    public const int DefaultDays = 90;

    /// <summary>
    ///     Ten years, which is longer than the history of nearly every repository and therefore means
    ///     "all of it". A ceiling rather than an unbounded value so that a caller passing a nonsense
    ///     number gets the whole history instead of a date arithmetic overflow.
    /// </summary>
    public const int MaxDays = 3650;

    /// <summary>The window reaching <paramref name="days" /> back from the newest recorded commit.</summary>
    public static HistoryWindow Ending(DateTimeOffset newest, int days)
    {
        int span = Math.Clamp(days, 1, MaxDays);
        return new HistoryWindow(newest.AddDays(-span), newest, span);
    }

    /// <summary>
    ///     The window in one phrase, for a reply that has to say what it covered. The anchor is named
    ///     where there is one: a reader who asked for 30 days and is shown a window ending two months
    ///     ago has learned that the index is stale, which is worth more than the ranking.
    /// </summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Since:yyyy-MM-dd} to {Until:yyyy-MM-dd}, the {Days} days to the newest recorded commit");
}
