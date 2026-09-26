namespace CodeExplorer.Reading;

/// <summary>
///     How a capped read tells a list that ends at its cap from one the cap cut short. It asks for one
///     row more than it reports, because a list that merely reaches the cap is otherwise
///     indistinguishable from one that was cut: a file with exactly the cap's number of rows would be
///     told there are more. The extra row is dropped, because the cap is the promise and not the query.
///     One spelling, so that every capped read means the same thing by "truncated".
/// </summary>
public static class Cap
{
    /// <summary>How many rows a read capped at <paramref name="cap" /> asks for: one past it.</summary>
    public static int Rows(int cap) => cap + 1;

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
