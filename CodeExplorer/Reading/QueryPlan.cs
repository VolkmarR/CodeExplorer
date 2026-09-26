using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer.Reading;

/// <summary>
///     Writes the query plan of a read to a directory, for working out where a slow one spends its
///     time. Off unless <c>CODEEXPLORER_EXPLAIN_DIR</c> names a directory, and the check is one read of
///     a static array's length, so a query pays nothing for this existing.
///     It explains the statement the reader actually built rather than one retyped beside it: a plan
///     read off a hand-copied query is a plan for a different query, which is the way this kind of
///     investigation usually goes wrong.
///     In Reading and not in Search, because every module's reads pass through
///     <see cref="IndexQuery.ReaderAsync" />, and it was a grep diagnostic only while grep was the
///     one query anybody had measured.
///     A diagnostic and not a feature. There is no endpoint and no configuration entry, because the
///     one caller is a developer with a shell who already has the environment variable.
/// </summary>
internal static class QueryPlan
{
    /// <summary>
    ///     Where a plan is written to right now, and for which reads: the environment variable's
    ///     directory takes every read for the life of the process, and each <see cref="Recording" />
    ///     takes its own server's while it is held. Replaced whole under <see cref="_gate" /> and read
    ///     without it, so the check on every query stays one read.
    /// </summary>
    private static volatile Target[] _targets =
        Environment.GetEnvironmentVariable("CODEEXPLORER_EXPLAIN_DIR") is { } fromEnvironment
            ? [new Target(fromEnvironment, null)]
            : [];

    private static readonly Lock _gate = new();

    /// <summary>Numbers every dump, so that no two share a name. See <see cref="Stamp" />.</summary>
    private static long _sequence;

    public static bool Enabled => _targets.Length > 0;

    /// <summary>
    ///     The same switch, held on for the length of one test and pointed at
    ///     <paramref name="directory" />. A test cannot use the environment variable itself: it is read
    ///     once, when this type is initialised, so setting it would only work for a test that happened
    ///     to run before the assembly's first index read, and nothing orders that. Asserting the query
    ///     count through some other counter would be asserting about a mechanism no developer uses.
    ///     It records only the reads of indexes under <paramref name="dataDirectory" />, the test's own
    ///     server's. Every test class runs a server of its own in this one process, in parallel, and a
    ///     recording that took every read in the process let a test count another class's statements as
    ///     its own whenever their parameters matched: the fixtures share repository and file names
    ///     (#286). The variable keeps taking everything, because a developer's one server is all there is.
    ///     Recordings overlap for the same reason, so each adds its entry and removes only its own. A
    ///     single slot that each one saved and restored let the first to finish switch recording off
    ///     under the second, whose own dump was then never written.
    /// </summary>
    internal static IDisposable Recording(string directory, string dataDirectory)
    {
        var target = new Target(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory))
                                           + Path.DirectorySeparatorChar);
        lock (_gate) _targets = [.. _targets, target];
        return new Restore(target);
    }

    /// <param name="Directory">Where the dumps go.</param>
    /// <param name="Scope">
    ///     The data directory whose reads it takes, ending in a separator; null for every read. Matched
    ///     against the connection's data source, the instance file under that directory's indexes.
    /// </param>
    private sealed record Target(string Directory, string? Scope)
    {
        public bool Takes(string dataSource) =>
            Scope is null || dataSource.StartsWith(Scope, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Restore(Target target) : IDisposable
    {
        public void Dispose()
        {
            lock (_gate)
            {
                var remaining = _targets.ToList();
                remaining.Remove(target);
                _targets = [.. remaining];
            }
        }
    }

    /// <summary>
    ///     The plan of a command that is about to be executed, named for the method that built it:
    ///     <c>IndexReader.FileAsync</c>, from the compiler's own <c>[CallerFilePath]</c> and
    ///     <c>[CallerMemberName]</c>. A label spelled at the call site would be a second name for the
    ///     query, free to drift from the method that owns it, and thirty of them would have to be
    ///     written before any of this measured anything. The file name stands in for the type because
    ///     the caller is a compiler constant and the type is not, and here a file is one type often
    ///     enough that the difference only shows where it does not matter.
    ///     This is the only way in (<see cref="IndexQuery.ReaderAsync" />, <see cref="IndexQuery.ScalarAsync" />),
    ///     so the statement explained is always the one about to run.
    /// </summary>
    public static Task DumpAsync(DuckDBCommand command, string file, string member,
        CancellationToken cancellationToken) =>
        DumpAsync((DuckDBConnection)command.Connection!,
            $"{Path.GetFileNameWithoutExtension(file)}.{member}", command.CommandText,
            [.. command.Parameters.Cast<DuckDBParameter>()], cancellationToken);

    /// <summary>
    ///     Runs <c>EXPLAIN ANALYZE</c> on <paramref name="sql" /> and writes it, with the statement and
    ///     its parameters, to a file named by <see cref="Stamp" />, into the directory of every recording that takes a
    ///     read of <paramref name="connection" />'s instance file.
    ///     The parameters are rebuilt rather than reused: a <see cref="DuckDBParameter" /> belongs to
    ///     the command it was added to, and handing the same instances to a second command is how this
    ///     would turn into a bug in the thing it is meant to be measuring.
    ///     Never throws into the search. A plan that could not be taken is a note in the file, because
    ///     a diagnostic that can fail a request is worse than no diagnostic.
    /// </summary>
    private static async Task DumpAsync(DuckDBConnection connection, string label, string sql,
        IReadOnlyList<DuckDBParameter> parameters, CancellationToken cancellationToken)
    {
        // One snapshot for the whole dump, so a recording that ends half-way cannot split it.
        string dataSource = connection.DataSource;
        string[] directories = [.. _targets.Where(target => target.Takes(dataSource)).Select(target => target.Directory)];
        if (directories.Length == 0) return;
        // DuckDB writes a profile to one path, so it goes to the first and is copied to the rest.
        string directory = directories[0];

        string stamp = Stamp(label);
        string text;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            // JSON and not the box-drawing tree: the tree is for a person reading one plan, and this
            // is for comparing operator times across a dozen of them. The profile covers the real
            // execution, so the timings are the query's own and not an EXPLAIN's warmed repeat.
            string json = Path.Combine(directory, stamp + ".json").Replace('\\', '/');
            await SetAsync(connection, "SET enable_profiling='json'", cancellationToken);
            await SetAsync(connection, $"SET profiling_output='{json}'", cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var parameter in parameters)
                    command.Parameters.Add(new DuckDBParameter(parameter.ParameterName, parameter.Value));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

        string profile = Path.Combine(directory, stamp + ".json");
        foreach (string target in directories)
        {
            try
            {
                if (target != directory)
                {
                    System.IO.Directory.CreateDirectory(target);
                    if (File.Exists(profile)) File.Copy(profile, Path.Combine(target, stamp + ".json"), true);
                }

                await File.WriteAllTextAsync(Path.Combine(target, stamp + ".sql.txt"), file.ToString(),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing to do about it and nothing worth failing a search over.
                Console.Error.WriteLine($"query plan not written: {ex.Message}");
            }
        }
    }

    private static async Task SetAsync(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The name of one dump: the time, a number no other dump in the process has, and the label.
    ///     The time and the label alone gave two reads of one method in one millisecond the same name,
    ///     so the second overwrote the first, and they sorted two methods in one millisecond by name
    ///     rather than by the order they ran. Nine digits, so the names still sort by number past any
    ///     run a process makes.
    /// </summary>
    internal static string Stamp(string label) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref _sequence):D9}-{Safe(label)}");

    private static string Safe(string label) =>
        string.Concat(label.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
