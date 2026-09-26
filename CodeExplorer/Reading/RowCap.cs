namespace CodeExplorer.Reading;

/// <summary>
///     How a capped read tells a list that ends at its cap from one the cap cut short. It asks for one
///     row more than it reports, because a list that merely reaches the cap is otherwise
///     indistinguishable from one that was cut: a file with exactly the cap's number of rows would be
///     told there are more. The extra row is dropped, because the cap is the promise and not the query.
///     One spelling, so that the reads capped this way cannot drift apart on what "truncated" means.
///     A read that counts its total with <c>count(*) OVER ()</c> knows it was cut without this.
/// </summary>
public static class RowCap
{
    /// <summary>How many rows a read capped at <paramref name="cap" /> asks for: one past it.</summary>
    public static int Limit(int cap) => cap + 1;

    /// <summary>
    ///     Cuts <paramref name="rows" /> back to <paramref name="cap" /> and says whether there was
    ///     anything to cut.
    /// </summary>
    public static bool Trim<T>(List<T> rows, int cap)
    {
        if (rows.Count <= cap) return false;
        rows.RemoveRange(cap, rows.Count - cap);
        return true;
    }
}
