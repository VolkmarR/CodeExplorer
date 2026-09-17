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
    ///     A statement with nothing to read back. The third thing every writer did to a connection it
    ///     was handed, and it was a private static in three files before it was here — which is the
    ///     same three places for a cancellation token to stop being threaded that the two above exist
    ///     to prevent.
    /// </summary>
    public static async Task ExecuteAsync(this DuckDBConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
