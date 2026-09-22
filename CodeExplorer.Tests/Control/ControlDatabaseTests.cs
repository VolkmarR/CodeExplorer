using System.Net;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The control database's native instance, kept open for the life of the process (#172).
///     DuckDB.NET keeps one instance per file only while some connection to it is open, and a call
///     that opened and closed its own connection tore the whole instance down behind it — file open,
///     WAL replay and a checkpoint on close, on every uncached lookup, twice for a write and once more
///     for its backup.
/// </summary>
public sealed class ControlDatabaseTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    /// <summary>
    ///     Proved with something only an instance remembers. <c>ATTACH</c> is instance-wide, so a
    ///     catalog attached through one connection is seen through the next only if the instance
    ///     lived between them. With nothing holding it, closing the first connection closes the
    ///     instance, the second opens a fresh one, and the marker is gone.
    /// </summary>
    [Fact]
    public async Task The_instance_outlives_every_connection_a_call_opens()
    {
        await _host.CreateProjectAsync("alpha");

        using (var first = await _host.OpenControlDatabaseAsync())
            await first.ExecuteAsync("ATTACH ':memory:' AS marker", Ct);

        using var second = await _host.OpenControlDatabaseAsync();
        using var command = second.CreateCommand();
        command.CommandText = "SELECT count(*) FROM duckdb_databases() WHERE database_name = 'marker'";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }

    /// <summary>
    ///     The price of an instance that lives: what one backup leaves attached, the next one finds. A
    ///     snapshot whose <c>COPY</c> threw never reached its <c>DETACH</c>, and while every call closed
    ///     the instance that was tidied up for free. Now it would make every later backup fail on the
    ///     name, and this file is the only copy of the credentials. The stray catalog is attached by
    ///     hand because a failing <c>COPY</c> cannot be arranged from outside.
    /// </summary>
    [Fact]
    public async Task A_backup_left_attached_by_a_failed_snapshot_does_not_stop_the_next()
    {
        await _host.CreateProjectAsync("alpha");
        using (var control = await _host.OpenControlDatabaseAsync())
            await control.ExecuteAsync("ATTACH ':memory:' AS backup", Ct);

        await _host.CreateProjectAsync("beta");

        _host.RestartWithoutControlDatabase();
        using var http = _host.CreateClient();
        using var restored = await http.GetAsync("/api/projects/beta", Ct);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }
}
