using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     The two things every search service does to a connection it was handed: build a command with
///     its parameters bound, and read a single count back. They are here rather than repeated as
///     private statics in each service because three copies of "bind the parameters" is three places
///     for a parameter to stop being bound, and an inlined value is how user text reaches the parser
///     in the first place.
/// </summary>
internal static class IndexQuery
{
    /// <summary>A command on this connection with every parameter bound. The caller disposes it.</summary>
    public static DuckDBCommand Query(this DuckDBConnection connection, string sql,
        IEnumerable<DuckDBParameter> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        return command;
    }

    /// <summary>
    ///     The single number a <c>SELECT count(...)</c> answers with. Read as a <see cref="long" />
    ///     because DuckDB answers a count with one and a caller that wants an <see cref="int" /> knows
    ///     its own bound.
    /// </summary>
    public static async Task<long> CountAsync(this DuckDBConnection connection, string sql,
        IEnumerable<DuckDBParameter> parameters, CancellationToken cancellationToken)
    {
        using var command = connection.Query(sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
