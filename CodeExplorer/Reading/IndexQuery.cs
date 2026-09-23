using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodeExplorer.Index;
using DuckDB.NET.Data;

namespace CodeExplorer.Reading;

/// <summary>
///     What every service does to a connection it was handed: build a command with its parameters
///     bound, read a single count back, run a statement, and spell the one kind of value that is
///     inlined rather than bound. They are here rather than repeated as private statics in each
///     service because three copies of "bind the parameters" is three places for a parameter to stop
///     being bound, and an inlined value is how user text reaches the parser in the first place —
///     which is why <see cref="Literal" /> is kept to values this deployment makes itself.
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
    ///     It stands in for <c>ExecuteReaderAsync</c> wherever a project's index is read on the way to
    ///     answering a request, so that the diagnostic covers every such query rather than the one that
    ///     was slow the day it was written — the query nobody suspects is exactly the one no
    ///     hand-placed dump is ever put in front of. A build and the control database are not covered
    ///     and are not meant to be: neither is on the path a slow answer is investigated from.
    ///     The label comes from the compiler rather than the call site for the reason
    ///     <see cref="QueryPlan.DumpAsync(DuckDBCommand,string,string,CancellationToken)" /> gives.
    ///     Nothing is awaited when the diagnostic is off: every read in the system passes through here,
    ///     and a state machine per read to test one static would be this costing something in the case
    ///     it is never switched on for.
    /// </summary>
    public static Task<DbDataReader> ReaderAsync(this DuckDBCommand command,
        CancellationToken cancellationToken, [CallerFilePath] string file = "",
        [CallerMemberName] string member = "") =>
        QueryPlan.Enabled
            ? ExplainedAsync(command, file, member, cancellationToken)
            : command.ExecuteReaderAsync(cancellationToken);

    private static async Task<DbDataReader> ExplainedAsync(DuckDBCommand command, string file, string member,
        CancellationToken cancellationToken)
    {
        await QueryPlan.DumpAsync(command, file, member, cancellationToken);
        return await command.ExecuteReaderAsync(cancellationToken);
    }

    /// <summary>
    ///     The one value a statement answers with, explained like any other read. Every <c>count(*)</c>
    ///     and every <c>EXISTS</c> on the query path goes through here for the reason the counts do:
    ///     a total is one number at the call site and a scan of a whole table underneath, which is the
    ///     shape that hides from a reader looking for what made an answer slow.
    /// </summary>
    public static Task<object?> ScalarAsync(this DuckDBCommand command, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "") =>
        QueryPlan.Enabled
            ? ExplainedScalarAsync(command, file, member, cancellationToken)
            : command.ExecuteScalarAsync(cancellationToken);

    private static async Task<object?> ExplainedScalarAsync(DuckDBCommand command, string file, string member,
        CancellationToken cancellationToken)
    {
        await QueryPlan.DumpAsync(command, file, member, cancellationToken);
        return await command.ExecuteScalarAsync(cancellationToken);
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
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The same statement from a build, which runs synchronously on a worker thread because the
    ///     git calls beside it do (<see cref="HistoryBuilder" />). The token is checked before the
    ///     statement and not threaded into it: DuckDB's synchronous <c>ExecuteNonQuery</c> takes none,
    ///     so a build is cancellable between statements and not inside one.
    /// </summary>
    public static void Execute(this DuckDBConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        cancellationToken.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     Text as a SQL string literal, quotes included, where nothing can be bound: <c>ATTACH</c>,
    ///     <c>COPY</c> and <c>SET</c> take no parameters, and a build's <see cref="Execute" /> binds none.
    ///     A slug is validated on the way into the control database and is still escaped rather than trusted.
    /// </summary>
    public static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>
    ///     The single number a <c>SELECT count(...)</c> answers with. Read as a <see cref="long" />
    ///     because DuckDB answers a count with one and a caller that wants an <see cref="int" /> knows
    ///     its own bound.
    /// </summary>
    public static async Task<long> CountAsync(this DuckDBConnection connection, string sql,
        IEnumerable<DuckDBParameter> parameters, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        await using var command = connection.Query(sql, parameters);
        return Convert.ToInt64(await command.ScalarAsync(cancellationToken, file, member),
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Whether <paramref name="sql" /> returns any row, asked as <c>EXISTS</c> around it. Never
    ///     <c>count(*) &gt; 0</c>: a count has to read every matching row, and a semi-join may stop at
    ///     the first — the difference between a probe and a scan on the history tables, which have no
    ///     index on path. Said here so a probe does not have to justify it again. What
    ///     <paramref name="sql" /> projects does not matter.
    /// </summary>
    public static async Task<bool> ExistsAsync(this DuckDBConnection connection, string sql,
        IEnumerable<DuckDBParameter> parameters, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        await using var command = connection.Query($"SELECT EXISTS ({sql})", parameters);
        return await command.ScalarAsync(cancellationToken, file, member) is true;
    }
}
