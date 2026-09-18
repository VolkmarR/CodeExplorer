using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    ///     Executes a read and, when <see cref="QueryPlan" /> is switched on, writes its plan first.
    ///     It stands in for <c>ExecuteReaderAsync</c> at every read of an index so that the diagnostic
    ///     covers every query rather than the one that was slow the day it was written — the query
    ///     nobody suspects is exactly the one no hand-placed dump is ever put in front of.
    ///     The label comes from the compiler rather than the call site for the reason
    ///     <see cref="QueryPlan.DumpAsync(DuckDBCommand,string,CancellationToken)" /> gives.
    /// </summary>
    public static async Task<DbDataReader> ReaderAsync(this DuckDBCommand command,
        CancellationToken cancellationToken, [CallerFilePath] string file = "",
        [CallerMemberName] string member = "")
    {
        if (QueryPlan.Enabled) await QueryPlan.DumpAsync(command, QueryPlan.Label(file, member), cancellationToken);
        return await command.ExecuteReaderAsync(cancellationToken);
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
        IEnumerable<DuckDBParameter> parameters, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        using var command = connection.Query(sql, parameters);
        // A count is a query like any other, and the one that scans the whole table to answer a total
        // is the kind that hides here: the caller shows one number and reads as cheap.
        if (QueryPlan.Enabled) await QueryPlan.DumpAsync(command, QueryPlan.Label(file, member), cancellationToken);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
