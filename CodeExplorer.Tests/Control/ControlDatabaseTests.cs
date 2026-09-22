using System.Net;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The control database's native instance, kept open for the life of the process (#172). Why it
///     is, and what that costs, is at <see cref="ControlDatabase" />'s anchor.
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

        await _host.ExecuteOnControlDatabaseAsync("ATTACH ':memory:' AS marker");

        using var second = await _host.OpenControlDatabaseAsync();
        Assert.Equal(1L, await second.CountAsync(
            "SELECT count(*) FROM duckdb_databases() WHERE database_name = 'marker'", [], Ct));
    }

    /// <summary>
    ///     A backup catalog a failed snapshot left attached does not stop the next backup from being
    ///     stored. Attached by hand, because a failing <c>COPY</c> cannot be arranged from outside.
    /// </summary>
    [Fact]
    public async Task A_backup_left_attached_by_a_failed_snapshot_does_not_stop_the_next()
    {
        await _host.CreateProjectAsync("alpha");
        await _host.ExecuteOnControlDatabaseAsync("ATTACH ':memory:' AS backup");

        await _host.CreateProjectAsync("beta");

        _host.RestartWithoutControlDatabase();
        using var http = _host.CreateClient();
        using var restored = await http.GetAsync("/api/projects/beta", Ct);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }
}
