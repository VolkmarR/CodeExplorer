using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CodeExplorer.McpProxy;

/// <summary>
///     The server of an evaluation package (docs/deployment/local-evaluation.md): where it listens,
///     where its data is, and how a proxy starts it when nothing answers. Read from the same
///     <c>appsettings.Evaluation.json</c> the server loads as its environment's settings, so the port and
///     the data directory are written down once for the proxy, the server it starts and
///     <c>start.cmd</c> alike — a second copy for the proxy would be the one left behind when a tester
///     moves the port off one that is taken.
///     The file sits beside the executable and is found there rather than in the current directory,
///     because Claude Desktop starts the proxy from a directory nobody chose. The server resolves the
///     relative paths in it against its own working directory, as every <c>Storage:DataDirectory</c> is
///     resolved, so both launchers start it in the <c>app</c> folder; the proxy resolves them against
///     that same folder.
/// </summary>
public sealed class LocalServer : IDisposable
{
    /// <summary>The environment the package runs the server in, which is what selects the settings file.</summary>
    public const string EnvironmentName = "Evaluation";

    public const string SettingsFile = "appsettings." + EnvironmentName + ".json";

    /// <summary>What the package ships, assumed for a settings file that names no URL.</summary>
    private const string DefaultUrl = "http://127.0.0.1:5000";

    /// <summary>The server, which the package's <c>app</c> folder holds beside this program.</summary>
    private const string ServerExecutable = "CodeExplorer.exe";

    /// <summary>
    ///     How long a started server has to answer. A judgement: the first start of a freshly unpacked
    ///     folder is the slow one, with an on-access virus scan reading every file the runtime loads, and
    ///     a minute leaves room for that many times over while a client is still waiting on the answer.
    /// </summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>One probe of a server that may not be there, which a listening one answers in milliseconds.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly string _appDirectory;
    private readonly string _executable;
    private readonly string _startLock;

    /// <summary>Serialises the starts of one proxy; the lock file does the same across proxies.</summary>
    private readonly SemaphoreSlim _starting = new(1, 1);

    private LocalServer(Uri baseAddress, string appDirectory, string executable, string dataDirectory)
    {
        BaseAddress = baseAddress;
        _appDirectory = appDirectory;
        _executable = executable;
        _startLock = Path.Combine(dataDirectory, "server-start.lock");
    }

    /// <summary>Where the server answers, as the root a project's endpoint is built on.</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    ///     The package's server, or null when there is no settings file beside the executable — a build
    ///     output rather than a package, where the server is someone's <c>dotnet run</c> and is neither
    ///     the proxy's to start nor its to configure.
    /// </summary>
    /// <param name="appDirectory">The folder holding the executable and the settings file.</param>
    /// <exception cref="InvalidOperationException">The file names a URL a proxy must not connect to.</exception>
    public static LocalServer? FromPackage(string appDirectory)
    {
        string file = Path.Combine(appDirectory, SettingsFile);
        if (!File.Exists(file)) return null;

        JsonElement settings;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), SettingsFormat);
            settings = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{file} is not valid JSON: {ex.Message}", ex);
        }

        // Kestrel takes several URLs separated by semicolons; the first is the one the proxy uses.
        string url = (Read(settings, "Urls") ?? DefaultUrl).Split(';', StringSplitOptions.TrimEntries)[0];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseAddress) || !StdioProxy.IsLoopback(baseAddress))
            throw new InvalidOperationException(
                $"Urls in {file} is '{url}'. The server must listen on this machine only, so set it back to "
                + $"{DefaultUrl} or another port on 127.0.0.1.");

        string dataDirectory = Path.GetFullPath(Read(settings, "Storage", "DataDirectory") ?? "data", appDirectory);
        return new LocalServer(baseAddress, appDirectory, Path.Combine(appDirectory, ServerExecutable),
            dataDirectory);
    }

    /// <summary>
    ///     The JSON .NET configuration accepts, which allows comments and trailing commas: a tester who
    ///     comments out a line of the settings file has a server that starts, and should have a proxy
    ///     that reads the same file.
    /// </summary>
    private static readonly JsonDocumentOptions SettingsFormat = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    ///     A string setting by its path of sections, matched without regard to case as .NET configuration
    ///     matches keys — read by hand, because the configuration library is a package this program
    ///     does without, and two settings are all it needs.
    /// </summary>
    private static string? Read(JsonElement settings, params string[] path)
    {
        var current = settings;
        foreach (string key in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            var match = current.EnumerateObject()
                .Where(property => string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value)
                .LastOrDefault();
            if (match is not { } found) return null;
            current = found;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>
    ///     Starts the server when nothing answers, and waits until it does. Null once it answers, and
    ///     otherwise why it does not.
    ///     Two proxies — one per project in Claude Desktop's configuration — start together, and DuckDB
    ///     lets one process hold a database file, so a second server would fail anyway. A lock file in the
    ///     data directory makes it impossible rather than merely harmless: whoever holds it checks again
    ///     before starting, so the proxy that waited finds the server the first one started. A file and
    ///     not a named mutex, because a mutex belongs to the thread that took it and this wait is async;
    ///     the operating system releases the file when its process dies, which is the abandoned-mutex case
    ///     handled for free.
    /// </summary>
    public async Task<string?> EnsureRunningAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await _starting.WaitAsync(cancellationToken);
        try
        {
            if (await AnswersAsync(http, cancellationToken)) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(_startLock)!);
            await using var held = await LockAsync(cancellationToken);
            if (held is null) return "another proxy has been starting it for longer than it should take";
            if (await AnswersAsync(http, cancellationToken)) return null;

            Process? process;
            try
            {
                process = Process.Start(StartInfo());
            }
            catch (Win32Exception ex)
            {
                return $"starting {_executable} failed: {ex.Message}";
            }

            using (process)
                return await AwaitAnswerAsync(process, http, cancellationToken);
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>
    ///     The server detached from the proxy, in a window of its own that is hidden. Through the shell
    ///     rather than as a child sharing the proxy's handles, because a child inherits them and the
    ///     server's console log would land on the proxy's stdout, which is the protocol stream. The
    ///     environment goes as a command-line switch for the same reason: the shell starts a process with
    ///     the caller's environment and takes no variables of its own.
    ///     The working directory is the <c>app</c> folder, as <c>start.cmd</c> sets it, because both the
    ///     relative paths in the settings file and the Data Protection key ring follow it: the framework
    ///     derives the key ring's application name from the content root, so a server started anywhere
    ///     else could not decrypt a credential the other one stored.
    /// </summary>
    private ProcessStartInfo StartInfo() => new(_executable, $"--environment {EnvironmentName}")
    {
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = _appDirectory,
    };

    private async Task<string?> AwaitAnswerAsync(Process? process, HttpClient http, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await AnswersAsync(http, cancellationToken)) return null;
            // A server that cannot bind its port or open its data exits at once; waiting out the minute
            // for it would only delay the sentence that sends the tester to start.cmd to read why.
            if (process is { HasExited: true }) return $"it exited with code {process.ExitCode} before answering";
            await Task.Delay(PollInterval, cancellationToken);
        }

        return $"it did not answer within {StartTimeout.TotalSeconds:0} seconds of being started";
    }

    /// <summary>
    ///     True when the server answers the project list, the cheapest request that proves it is
    ///     CodeExplorer and serving rather than merely holding the port.
    /// </summary>
    private async Task<bool> AnswersAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await http.GetAsync(new Uri(BaseAddress, "api/projects"), probe.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Safe to swallow: nothing listening is the answer this probe exists to give.
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Safe to swallow: a port that accepts and never answers is not a server that is running.
            return false;
        }
    }

    public void Dispose() => _starting.Dispose();

    /// <summary>
    ///     The lock file opened for exclusive use, or null when another process held it for longer than a
    ///     start can take — a proxy hung rather than starting, which this one should not wait out.
    /// </summary>
    private async Task<FileStream?> LockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartTimeout + StartTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_startLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Safe to swallow: another proxy holds the lock while it starts the server, and the
                // loop is the wait for it.
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (IOException)
            {
                // Safe to swallow: past the deadline, null is the answer and the caller says why.
                return null;
            }
        }
    }
}
