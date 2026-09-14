using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The warm-up: one call that attaches every project, for the external cron that fires it before
///     working hours (#9). It sits beside the refresh because ADR-0005 puts the two endpoints
///     together, and the test mirrors the folder its code is in.
/// </summary>
public sealed class WarmUpTests : IDisposable
{
    private TestHost? _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host?.Dispose();

    [Fact]
    public async Task The_warm_up_endpoint_attaches_every_project()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        await host.IndexedProjectAsync("beta", Repository("class Beta;\n", "two"));
        await host.CreateProjectAsync("never-built");
        host.DeleteIndexFile("alpha");
        host.DeleteIndexFile("beta");
        host.Restart();

        using var http = host.Factory.CreateClient();
        using var response = await http.PostAsync("/api/warmup", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warmed = await response.Content.ReadFromJsonAsync<List<WarmedProject>>(Ct);
        Assert.NotNull(warmed);
        Assert.True(Assert.Single(warmed, w => w.Project == "alpha").Ready);
        Assert.True(Assert.Single(warmed, w => w.Project == "beta").Ready);
        // Never indexed, so there is nothing to restore and nothing wrong: the operator refreshes it.
        var cold = Assert.Single(warmed, w => w.Project == "never-built");
        Assert.False(cold.Ready);
        Assert.Null(cold.Error);
        Assert.True(host.Indexes.HasIndex("alpha"));
        Assert.True(host.Indexes.HasIndex("beta"));
    }

    [Fact]
    public async Task The_background_warm_up_attaches_every_project_without_anyone_connecting()
    {
        var host = Start(SearchEngine.Substring, true);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        await host.IndexedProjectAsync("beta", Repository("class Beta;\n", "two"));
        host.DeleteIndexFile("alpha");
        host.DeleteIndexFile("beta");

        host.Restart();
        // Awaited rather than polled: the service hands its pass back the way every other background
        // task here does, so the test waits on the work and not on a sleep.
        await host.WarmUpService.Warmed.WaitAsync(Ct);

        // No request, no MCP client, no operator page: the restores happened because the host started.
        Assert.True(host.Indexes.HasIndex("alpha"));
        Assert.True(host.Indexes.HasIndex("beta"));
    }

    [Fact]
    public async Task It_does_nothing_unless_it_is_switched_on()
    {
        var host = Start(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", Repository("class Alpha;\n"));
        host.DeleteIndexFile("alpha");

        host.Restart();
        await host.WarmUpService.Warmed.WaitAsync(Ct);

        // Off is the default, because under scale to zero a warm-up on every wake would pay for every
        // project's restore on each one — the cost lazy attach exists to avoid (#9).
        Assert.False(host.Indexes.HasIndex("alpha"));
    }

    /// <summary>One repository of one file. The slug names the fixture on disk too, so two projects need two.</summary>
    private static Dictionary<string, Dictionary<string, string>> Repository(string content, string slug = "one") =>
        new() { [slug] = new Dictionary<string, string> { ["src/A.cs"] = content } };

    private TestHost Start(SearchEngine engine, bool warmUpOnStart = false) =>
        _host = new TestHost(engine, warmUpOnStart: warmUpOnStart);
}
