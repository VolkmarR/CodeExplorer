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
}
