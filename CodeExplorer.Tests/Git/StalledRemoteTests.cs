using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeExplorer.Control;
using CodeExplorer.Index;
using CodeExplorer.Refresh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Runs alone, after every parallel test: the stall limit is a libgit2 setting and therefore one
///     value for the whole process, so a host built beside this one would put the shipped default back
///     while this test waits on its lowered one.
/// </summary>
[CollectionDefinition(nameof(ProcessWideGitSettings), DisableParallelization = true)]
public sealed class ProcessWideGitSettings;

/// <summary>
///     A remote that accepts the connection and then says nothing (#231). libgit2 reports progress only
///     while data arrives, so nothing but a transfer limit ends such a clone, and without one the
///     refresh — and the project's refresh slot with it — waited until the process restarted. The
///     remotes are raw TCP listeners on loopback, so the suite still never touches the network.
/// </summary>
[Collection(nameof(ProcessWideGitSettings))]
public sealed class StalledRemoteTests : IDisposable
{
    private const int StallSeconds = 2;

    private readonly TestHost _host = new(SearchEngine.Substring, transferStallSeconds: StallSeconds);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentBag<Socket> _accepted = [];

    public StalledRemoteTests() => _listener.Start();

    private string RemoteUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/remote.git";

    public void Dispose()
    {
        foreach (var socket in _accepted) socket.Dispose();
        _listener.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task A_remote_that_stops_responding_is_skipped_within_the_stall_limit()
    {
        // Accepted and then left alone: nothing is read from the connection and nothing written to it.
        Serve(_ => { });

        string healthy = _host.CreateGitRepository("healthy", new Dictionary<string, string> { ["a.cs"] = "class A {}" });
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "healthy", healthy);
        await _host.AddRepositoryAsync("alpha", "stalled", RemoteUrl);

        var refresh = _host.RefreshAsync("alpha");
        var elapsed = await TimedAsync(refresh);
        var summary = await refresh;

        string skipped = Assert.Single(summary.Skipped);
        Assert.Contains("'stalled'", skipped, StringComparison.Ordinal);
        Assert.Contains("stopped responding", skipped, StringComparison.Ordinal);
        Assert.Equal(1, summary.Files);
        // Far below the shipped default, so the lowered limit and not some other timeout ended it.
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"The refresh took {elapsed}.");
    }

    [Fact]
    public async Task A_remote_that_keeps_sending_is_not_cut_off_by_the_stall_limit()
    {
        // An empty repository's advertisement, sent a piece at a time with gaps shorter than the limit
        // but a total well past it. The limit bounds each wait for the remote, not the transfer, so this
        // is a slow clone that must finish rather than a stalled one.
        Serve(DripEmptyAdvertisement);
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "slow", RemoteUrl);

        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var elapsed = await TimedAsync(_host.WaitForRefreshesAsync());

        // An empty remote is the only repository, so the refresh fails as a whole — with the empty
        // remote's sentence, which is the proof the clone ran to the end instead of timing out.
        string error = Assert.IsType<string>((await _host.RefreshStatusAsync("alpha")).Error);
        Assert.Contains("no commits yet", error, StringComparison.Ordinal);
        Assert.DoesNotContain("stopped responding", error, StringComparison.Ordinal);
        Assert.True(elapsed > TimeSpan.FromSeconds(StallSeconds * 2), $"The refresh took only {elapsed}.");
    }

    [Fact]
    public async Task A_remote_slow_to_answer_with_an_error_is_reported_with_that_error()
    {
        // A 404 in answer to the reference discovery, a piece at a time, for longer than the limit in
        // all but with every gap inside it. No callback fires during the discovery, so how long the
        // remote took cannot tell this from a stall; the error it ended with can (#289).
        Serve(socket => Drip(socket, "",
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "missing", RemoteUrl);

        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var elapsed = await TimedAsync(_host.WaitForRefreshesAsync());

        string error = Assert.IsType<string>((await _host.RefreshStatusAsync("alpha")).Error);
        Assert.Contains("404", error, StringComparison.Ordinal);
        Assert.DoesNotContain("stopped responding", error, StringComparison.Ordinal);
        // Past the limit, or the remote was not slow enough for the elapsed time to have misled.
        Assert.True(elapsed > TimeSpan.FromSeconds(StallSeconds), $"The refresh took only {elapsed}.");
    }

    [Fact]
    public async Task A_refresh_cancelled_on_a_stalled_remote_stops_well_within_the_stall_limit()
    {
        // A limit no test would wait out, so only the cancellation can end the refresh in time. Its own
        // host, because the class's has the lowered limit and building it would put that one back.
        using var host = new TestHost(SearchEngine.Substring, transferStallSeconds: 120);
        Serve(_ => { });
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "stalled", RemoteUrl);
        var project = (await host.Services.GetRequiredService<ControlDatabase>()
            .FindAsync("alpha", TestContext.Current.CancellationToken))!;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Cancelled a second into the clone, by which time libgit2 is waiting on the silent remote and
        // calls nothing back that could carry the cancellation to it (#289).
        var refresh = host.Services.GetRequiredService<ProjectRefresh>().RunAsync(project, progress =>
        {
            if (progress.Phase == "Fetching 'stalled'") cancel.CancelAfter(TimeSpan.FromSeconds(1));
        }, cancel.Token);
        var elapsed = await TimedAsync(Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh));

        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"The cancelled refresh took {elapsed}.");
    }

    /// <summary>
    ///     Bounded here rather than by the runner, so a regression fails the test instead of hanging the
    ///     suite: a libgit2 call that never returns cannot be cancelled by the token either.
    /// </summary>
    private static async Task<TimeSpan> TimedAsync(Task refresh)
    {
        var clock = Stopwatch.StartNew();
        await refresh.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return clock.Elapsed;
    }

    /// <summary>
    ///     Accepts every connection, hands it to <paramref name="handle" />, and keeps it open. Dedicated
    ///     threads and blocking calls rather than the thread pool: this class runs after the rest of the
    ///     suite, whose leftovers can starve the pool for longer than a gap in the drip is allowed to last.
    /// </summary>
    private void Serve(Action<Socket> handle) => StartThread(() =>
    {
        try
        {
            while (true)
            {
                var socket = _listener.AcceptSocket();
                _accepted.Add(socket);
                StartThread(() => handle(socket));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Safe to swallow: Dispose stops the listener, which is what ends this loop.
        }
    });

    private static void StartThread(Action body) => new Thread(new ThreadStart(body)) { IsBackground = true }.Start();

    /// <summary>
    ///     Answers the smart-HTTP reference discovery of a repository with no refs, a few bytes at a time.
    ///     Protocol v0: the service line, a flush, the capabilities line an empty repository sends in place
    ///     of its first ref, and a closing flush. With no ref to want, libgit2 asks for nothing further.
    /// </summary>
    private static void DripEmptyAdvertisement(Socket socket) => Drip(socket,
        "HTTP/1.1 200 OK\r\nContent-Type: application/x-git-upload-pack-advertisement\r\n"
        + "Cache-Control: no-cache\r\nConnection: close\r\n\r\n",
        "001e# service=git-upload-pack\n0000"
        + "003e0000000000000000000000000000000000000000 capabilities^{}\0\n"
        + "0000");

    /// <summary>
    ///     Reads one request, sends <paramref name="upfront" /> at once and then
    ///     <paramref name="dripped" /> twelve bytes at a time, 600 ms apart: every gap well inside the
    ///     limit, and a response of 60 bytes or more well past it in all.
    /// </summary>
    private static void Drip(Socket socket, string upfront, string dripped)
    {
        try
        {
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = socket.Receive(buffer);
                if (read == 0) return;
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            if (upfront.Length > 0) socket.Send(Encoding.ASCII.GetBytes(upfront));
            foreach (var piece in Encoding.ASCII.GetBytes(dripped).Chunk(12))
            {
                Thread.Sleep(600);
                socket.Send(piece);
            }

            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // Safe to swallow: the client hung up or the test ended, and the assertions are on the refresh.
        }
    }
}
