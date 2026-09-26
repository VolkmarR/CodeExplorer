using System.Net;
using System.Net.Http.Json;
using CodeExplorer.Index;
using CodeExplorer.Reading;
using DuckDB.NET.Data;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The overview page's excluded-paths setting as the control database holds it (#216): empty until
///     an operator writes one, stored normalised, refused whole when it breaks a limit, kept across a
///     restart and gone with its project.
/// </summary>
public sealed class ExcludedPathsTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    /// <summary>What the endpoint reads and writes, mirrored rather than shared with the internal record.</summary>
    private sealed record Body(string[] Patterns);

    [Fact]
    public async Task A_new_project_excludes_nothing()
    {
        await _host.CreateProjectAsync("alpha");

        Assert.Empty(await ReadAsync("alpha"));
    }

    [Fact]
    public async Task A_saved_list_is_trimmed_deduplicated_and_kept_in_order()
    {
        await _host.CreateProjectAsync("alpha");

        var (status, saved) = await WriteAsync("alpha",
            ["  **/*.rc ", "", @"**\AssemblyInfo.*", "**/*.RC", "**/*.verified.txt"]);

        Assert.Equal(HttpStatusCode.OK, status);
        // A repeat differing only by case is a repeat: matching is case-insensitive.
        string[] expected = ["**/*.rc", "**/AssemblyInfo.*", "**/*.verified.txt"];
        Assert.Equal(expected, saved);
        Assert.Equal(expected, await ReadAsync("alpha"));
    }

    [Fact]
    public async Task A_list_over_the_limit_is_refused_and_the_stored_one_kept()
    {
        await _host.CreateProjectAsync("alpha");
        await WriteAsync("alpha", ["**/*.rc"]);

        var (status, _) = await WriteAsync("alpha",
            Enumerable.Range(0, ExcludedPaths.MaxPatterns + 1).Select(i => $"**/{i}.txt").ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(["**/*.rc"], await ReadAsync("alpha"));
    }

    [Fact]
    public async Task A_pattern_that_cannot_be_matched_is_refused_by_name_and_nothing_is_stored()
    {
        await _host.CreateProjectAsync("alpha");
        await WriteAsync("alpha", ["**/*.rc"]);

        using var http = _host.CreateClient();
        using var response = await http.PutAsJsonAsync("/api/projects/alpha/excluded-paths",
            new { patterns = _reversedRange }, Ct);

        // Refused as a sentence naming the pattern, rather than stored and failing every overview after.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'**/[z-a].txt'", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(["**/*.rc"], await ReadAsync("alpha"));
    }

    /// <summary>A class GLOB would take and RE2 refuses, beside a pattern that is fine.</summary>
    private static readonly string[] _reversedRange = ["**/*.md", "**/[z-a].txt"];

    /// <summary>
    ///     A connection that cannot run the check is the server's fault, not the pattern's (#296). It is
    ///     provoked the way CODING_STANDARDS says a pooled connection breaks: another connection detaches
    ///     the catalog it is bound to, so its next statement fails with a Binder Error that says nothing
    ///     about RE2.
    /// </summary>
    [Fact]
    public async Task A_check_that_fails_for_another_reason_throws_rather_than_refusing_the_pattern()
    {
        string path = _host.ScratchFile("check.duckdb");
        // The data folder is made when the host starts, and this check needs no host.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var other = new DuckDBConnection($"Data Source={path}");
        await other.OpenAsync(Ct);
        // The same file, so DuckDB.NET hands both connections one database instance.
        using var connection = new DuckDBConnection($"Data Source={path}");
        await connection.OpenAsync(Ct);
        string attached = _host.ScratchFile("attached.duckdb").Replace("'", "''");
        await ExecuteAsync(other, $"ATTACH '{attached}' AS attached");
        await ExecuteAsync(connection, "USE attached");
        await ExecuteAsync(other, "DETACH attached");

        await Assert.ThrowsAsync<DuckDBException>(() => ExcludedPaths.RefusedAsync(connection, "**/*.rc", Ct));
    }

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task The_setting_survives_a_restart_and_goes_with_its_project()
    {
        await _host.CreateProjectAsync("alpha");
        await WriteAsync("alpha", ["**/*.rc"]);

        _host.Restart();
        Assert.Equal(["**/*.rc"], await ReadAsync("alpha"));

        using (var http = _host.CreateClient())
        using (var deleted = await http.DeleteAsync("/api/projects/alpha", Ct))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // The same slug again is a new project, and must not open with the old one's exclusions.
        await _host.CreateProjectAsync("alpha");
        Assert.Empty(await ReadAsync("alpha"));
    }

    private async Task<string[]> ReadAsync(string slug)
    {
        using var http = _host.CreateClient();
        var body = await http.GetFromJsonAsync<Body>($"/api/projects/{slug}/excluded-paths", Ct);
        return Assert.IsType<Body>(body).Patterns;
    }

    private async Task<(HttpStatusCode Status, string[]? Saved)> WriteAsync(string slug, string[] patterns)
    {
        using var http = _host.CreateClient();
        using var response = await http.PutAsJsonAsync($"/api/projects/{slug}/excluded-paths", new { patterns }, Ct);
        return response.IsSuccessStatusCode
            ? (response.StatusCode, (await response.Content.ReadFromJsonAsync<Body>(Ct))?.Patterns)
            : (response.StatusCode, null);
    }
}
