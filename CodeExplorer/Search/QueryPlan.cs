using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     Writes the query plan of a search to a directory, for working out where a slow one spends its
///     time. Off unless <c>CODEEXPLORER_EXPLAIN_DIR</c> names a directory, and the check is one string
///     comparison on a static, so a search pays nothing for this existing.
///     It explains the statement the search actually built rather than one retyped beside it: a plan
///     read off a hand-copied query is a plan for a different query, which is the way this kind of
///     investigation usually goes wrong.
///     A diagnostic and not a feature. There is no endpoint and no configuration entry, because the
///     one caller is a developer with a shell who already has the environment variable.
/// </summary>
internal static class QueryPlan
{
    private static readonly string? Directory = Environment.GetEnvironmentVariable("CODEEXPLORER_EXPLAIN_DIR");

    public static bool Enabled => Directory is not null;

    /// <summary>
    ///     Runs <c>EXPLAIN ANALYZE</c> on <paramref name="sql" /> and writes it, with the statement and
    ///     its parameters, to a file named for <paramref name="label" /> and the time.
    ///     The parameters are rebuilt rather than reused: a <see cref="DuckDBParameter" /> belongs to
    ///     the command it was added to, and handing the same instances to a second command is how this
    ///     would turn into a bug in the thing it is meant to be measuring.
    ///     Never throws into the search. A plan that could not be taken is a note in the file, because
    ///     a diagnostic that can fail a request is worse than no diagnostic.
    /// </summary>
    public static async Task DumpAsync(DuckDBConnection connection, string label, string sql,
        IReadOnlyList<DuckDBParameter> parameters, CancellationToken cancellationToken)
    {
        if (Directory is null) return;

        string stamp = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Safe(label)}");
        string text;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // JSON and not the box-drawing tree: the tree is for a person reading one plan, and this
            // is for comparing operator times across a dozen of them. The profile covers the real
            // execution, so the timings are the query's own and not an EXPLAIN's warmed repeat.
            string json = Path.Combine(Directory, stamp + ".json").Replace('\\', '/');
            await SetAsync(connection, "SET enable_profiling='json'", cancellationToken);
            await SetAsync(connection, $"SET profiling_output='{json}'", cancellationToken);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var parameter in parameters)
                    command.Parameters.Add(new DuckDBParameter(parameter.ParameterName, parameter.Value));
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) { }
            }
            finally
            {
                // Left on, every later query on this connection would overwrite the file.
                await SetAsync(connection, "SET profiling_output=''", cancellationToken);
                await SetAsync(connection, "PRAGMA disable_profiling", cancellationToken);
            }

            text = $"profile written beside this file as {stamp}.json";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            text = "profiling failed: " + ex.Message;
        }

        var file = new System.Text.StringBuilder();
        file.Append(CultureInfo.InvariantCulture, $"-- {label} at {DateTimeOffset.UtcNow:O}\n\n");
        foreach (var parameter in parameters)
            file.Append(CultureInfo.InvariantCulture, $"-- ${parameter.ParameterName} = {parameter.Value}\n");
        file.Append('\n').Append(sql).Append("\n\n").Append(text);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(Directory, stamp + ".sql.txt"), file.ToString(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing to do about it and nothing worth failing a search over.
            Console.Error.WriteLine($"query plan not written: {ex.Message}");
        }
    }

    private static async Task SetAsync(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Safe(string label) =>
        string.Concat(label.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
