namespace CodeExplorer.Reading;

/// <summary>How far into an ordering a page starts, for every read that pages one.</summary>
public static class Paging
{
    /// <summary>
    ///     The rows ahead of <paramref name="page" /> (1-based) in pages of <paramref name="size" />. A
    ///     <c>long</c>, because a caller's page is clamped only from below: a large one times the size
    ///     wrapped an <c>int</c> negative, which DuckDB refused as an error instead of answering an empty
    ///     page past the end (#233). One spelling, because the overflow was fixed five times by hand.
    /// </summary>
    public static long Skip(int page, int size) => (page - 1L) * size;
}
