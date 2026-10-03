namespace CodeExplorer.McpProxy;

/// <summary>
///     <c>CodeExplorer.McpProxy.exe &lt;project&gt;</c>: the process Claude Desktop starts for one project
///     (docs/deployment/local-evaluation.md). A program of its own beside the server rather than a mode of
///     it, so that nothing the server carries — a host that logs to the console above all — can reach the
///     stdout this process speaks the protocol on: every byte it writes there is <see cref="StdioProxy" />'s.
///     No port switch. The port is the server's, written once in the package's settings file, and a
///     second place to set it is the place left behind when a tester moves the server off a port that is
///     taken.
/// </summary>
internal static class Program
{
    /// <summary>
    ///     Where a proxy outside a package looks: the address Kestrel serves a <c>dotnet run</c> on when
    ///     nothing names another (README, "Running it"), spelled as Kestrel spells it.
    /// </summary>
    private static readonly Uri DevelopmentServer = new("http://localhost:5000/");

    /// <summary>
    ///     A long tool call is bounded by the server's own limits and by the client, which cancels what it
    ///     stops waiting for. HttpClient's default 100 seconds would cut off a call the client was still
    ///     waiting on, and answer it with a timeout the server never had.
    /// </summary>
    private static readonly TimeSpan NoTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    ///     Short, because the address is on this machine: it is refused at once or accepted at once, and
    ///     a connect that hangs is a firewall product in the way, which waiting does not fix.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        if (args is not [var project] || string.IsNullOrWhiteSpace(project) || project.StartsWith('-'))
        {
            await Console.Error.WriteLineAsync(
                "Usage: CodeExplorer.McpProxy <project-slug>. One argument, the slug of the project to connect to.");
            return 1;
        }

        LocalServer? server;
        try
        {
            server = LocalServer.FromPackage(AppContext.BaseDirectory);
        }
        catch (InvalidOperationException ex)
        {
            // Safe to swallow: the sentence is all a tester can act on, and standard error is where
            // Claude Desktop keeps it, in the server's log.
            await Console.Error.WriteLineAsync($"codeexplorer proxy: {ex.Message}");
            return 1;
        }

        // No system proxy: every address this may reach is on this machine, so one has nothing to add,
        // and a corporate configuration that does not exempt loopback would only break it.
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = ConnectTimeout })
        {
            Timeout = NoTimeout,
        };

        using (server)
        {
            using var proxy = new StdioProxy(Console.OpenStandardInput(), Console.OpenStandardOutput(), http,
                server?.BaseAddress ?? DevelopmentServer, project, server, Console.Error);
            await proxy.RunAsync(CancellationToken.None);
        }

        return 0;
    }
}
