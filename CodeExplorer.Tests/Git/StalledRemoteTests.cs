using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeExplorer.Index;
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

        var (summary, elapsed) = await RefreshAsync("alpha");

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
        var clock = Stopwatch.StartNew();
        await _host.WaitForRefreshesAsync().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        clock.Stop();

        // An empty remote is the only repository, so the refresh fails as a whole — with the empty
        // remote's sentence, which is the proof the clone ran to the end instead of timing out.
        string error = Assert.IsType<string>((await _host.RefreshStatusAsync("alpha")).Error);
        Assert.Contains("no commits yet", error, StringComparison.Ordinal);
        Assert.DoesNotContain("stopped responding", error, StringComparison.Ordinal);
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(StallSeconds * 2), $"The refresh took only {clock.Elapsed}.");
    }

    /// <summary>
    ///     Bounded here rather than by the runner, so a regression fails the test instead of hanging the
    ///     suite: a libgit2 call that never returns cannot be cancelled by the token either.
    /// </summary>
    private async Task<(IndexSummary Summary, TimeSpan Elapsed)> RefreshAsync(string project)
    {
        var clock = Stopwatch.StartNew();
        var summary = await _host.RefreshAsync(project)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return (summary, clock.Elapsed);
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

    private static void StartThread(Action body) => new Thread(() => body()) { IsBackground = true }.Start();

    /// <summary>
    ///     Answers the smart-HTTP reference discovery of a repository with no refs, a few bytes at a time.
    ///     Protocol v0: the service line, a flush, the capabilities line an empty repository sends in place
    ///     of its first ref, and a closing flush. With no ref to want, libgit2 asks for nothing further.
    /// </summary>
    private static void DripEmptyAdvertisement(Socket socket)
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

            socket.Send(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/x-git-upload-pack-advertisement\r\n"
                + "Cache-Control: no-cache\r\nConnection: close\r\n\r\n"));
            byte[] body = Encoding.ASCII.GetBytes("001e# service=git-upload-pack\n0000"
                                                  + "003e0000000000000000000000000000000000000000 capabilities^{}\0\n"
                                                  + "0000");
            // Nine pieces 600 ms apart: every gap well inside the limit, the whole well past twice it.
            foreach (var piece in body.Chunk(12))
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
