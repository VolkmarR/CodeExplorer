using System.Data.Common;

namespace CodeExplorer;

/// <summary>
///     Reads a column by name instead of by position. Every reader in this codebase built a record from
///     ordinals, so adding a column to a <c>SELECT</c>, or reordering one, silently shifted every read
///     after it: two columns of the same type swap their values with no error anywhere, and a build
///     that changes a projection breaks a caller in another file.
///     The getter for each column is the same one the ordinal version called — this resolves where a
///     value is, never what it is — so a type that was wrong before is still wrong, and loudly.
///     Every selected expression therefore needs a name: alias anything that is not a bare column.
/// </summary>
internal static class ReaderColumns
{
    /// <summary>
    ///     For a column whose null is the row's meaning rather than a value: the left join that carries
    ///     the totals of an empty page answers null for every column of the missing side.
    /// </summary>
    public static bool IsNull(this DbDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column));

    public static string Text(this DbDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    /// <summary>For a nullable column, where null is an answer rather than a missing row.</summary>
    public static string? TextOrNull(this DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static bool Flag(this DbDataReader reader, string column) =>
        reader.GetBoolean(reader.GetOrdinal(column));

    /// <summary>
    ///     False when the column is null. A scalar subquery over a table with no rows answers null
    ///     rather than false, which is how an index still being built reads.
    /// </summary>
    public static bool FlagOrFalse(this DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }

    /// <summary>An <c>INTEGER</c> column. A <c>BIGINT</c> one is <see cref="Int64" />, cast at the call site.</summary>
    public static int Int32(this DbDataReader reader, string column) =>
        reader.GetInt32(reader.GetOrdinal(column));

    public static long Int64(this DbDataReader reader, string column) =>
        reader.GetInt64(reader.GetOrdinal(column));

    public static double Double(this DbDataReader reader, string column) =>
        reader.GetDouble(reader.GetOrdinal(column));

    /// <summary>
    ///     A column selected as <c>epoch(&lt;timestamptz&gt;)</c>, back as the instant it names. Seconds as a
    ///     double are the one representation of a <c>TIMESTAMP WITH TIME ZONE</c> that does not depend on
    ///     whether the ICU extension is loaded to decide the session time zone, so a query that wants an
    ///     instant rather than a <see cref="Timestamp" /> selects it this way and reads it here.
    /// </summary>
    public static DateTimeOffset EpochInstant(this DbDataReader reader, string column) =>
        DateTimeOffset.FromUnixTimeSeconds((long)reader.Double(column));

    /// <summary>
    ///     A <c>TIMESTAMP WITH TIME ZONE</c> column. Read through <c>GetFieldValue</c> and not
    ///     <c>GetDateTime</c>, because that is the getter the driver hands a
    ///     <see cref="DateTimeOffset" /> back from; the rest of this file resolves where a value is and
    ///     this one is no different.
    /// </summary>
    public static DateTimeOffset Timestamp(this DbDataReader reader, string column) =>
        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(column));

    /// <summary>
    ///     For a nullable timestamp: a repository whose history was never walked, a scope holding no
    ///     commit. Null is an answer there and not a missing row.
    /// </summary>
    public static DateTimeOffset? TimestampOrNull(this DbDataReader reader, string column) =>
        reader.IsNull(column) ? null : reader.Timestamp(column);

    /// <summary>
    ///     The commit a row is attributed to, from the four columns every such join selects, or null
    ///     where there is none — a line the build could not attribute, a file no commit touched within
    ///     the imported history. It was assembled four different ways before it was here, which is four
    ///     places for a column to be renamed in the SQL and read from the old name in one of them.
    ///     A query that selects two attributions in one row prefixes both and says which it wants; the
    ///     columns are then <c>first_sha</c>, <c>first_author_name</c> and so on, spelled that way in
    ///     the <c>SELECT</c> so that one set of names is read by one helper.
    /// </summary>
    /// <param name="reader">The row.</param>
    /// <param name="prefix">What the aliases carry in front of the four names, or nothing.</param>
    public static AttributedBy? Attribution(this DbDataReader reader, string prefix = "") =>
        reader.IsNull(prefix + "sha")
            ? null
            : new AttributedBy(reader.Text(prefix + "sha"), reader.Text(prefix + "author_name"),
                reader.Timestamp(prefix + "authored_at"), reader.Text(prefix + "subject"));
}
