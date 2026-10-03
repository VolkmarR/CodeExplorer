using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CodeExplorer.McpProxy;
using CodeExplorer.Index;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The stdio proxy Claude Desktop starts (docs/deployment/local-evaluation.md), driven through
///     in-memory pipes against the in-process server where the server's own answer is the subject, and
///     against a scripted handler where what is asserted is a behaviour this server does not produce on
///     its own: it runs the SDK's stateless transport, so it hands out no session to carry, lose or end,
///     and it answers too quickly to hold one request open while another overtakes it.
/// </summary>
public sealed class StdioProxyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Far past anything here takes, so only a proxy that is stuck reaches it.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Initialize_list_and_call_reach_the_project_through_the_proxy()
    {
        await _host.CreateProjectAsync("proxied");
        await using var proxy = Proxy("proxied");

        await proxy.SendAsync(Initialize(1));
        var initialized = await proxy.ReadAsync();
        Assert.Equal(1, (int)initialized["id"]!);
        Assert.Equal("2025-11-25", (string?)initialized["result"]!["protocolVersion"]);

        await proxy.SendAsync(Initialized);
        await proxy.SendAsync(Request(2, "tools/list"));
        await proxy.SendAsync(Request(3, "tools/call", """{"name":"which_project","arguments":{}}"""));
        var answers = new[] { await proxy.ReadAsync(), await proxy.ReadAsync() }
            .ToDictionary(answer => (int)answer["id"]!);

        var tools = answers[2]["result"]!["tools"]!.AsArray().Select(tool => (string?)tool!["name"]);
        Assert.Contains("which_project", tools);
        Assert.Contains("slug: proxied", (string?)answers[3]["result"]!["content"]![0]!["text"]);
        Assert.Empty(await proxy.CloseAsync());
    }

    [Fact]
    public async Task A_notification_is_answered_with_no_line()
    {
        await _host.CreateProjectAsync("quiet");
        await using var proxy = Proxy("quiet");

        await proxy.SendAsync(Initialize(1));
        _ = await proxy.ReadAsync();
        await proxy.SendAsync(Initialized);
        await proxy.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":99}}""");

        Assert.Empty(await proxy.CloseAsync());
    }

    /// <summary>
    ///     The call sent first is held by the server until the one sent after it has been answered, so
    ///     a proxy that waited for each answer before reading the next line never sends the second and
    ///     the test runs out of patience instead of passing.
    /// </summary>
    [Fact]
    public async Task A_slow_request_does_not_hold_up_the_one_sent_after_it()
    {
        var overtaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new ScriptedServer(async (_, body) =>
        {
            int? id = (int?)body?["id"];
            if (id == 2) await overtaken.Task.WaitAsync(Patience, Ct);
            if (id == 3) overtaken.SetResult();
            return id is null ? ScriptedServer.Accepted() : ScriptedServer.Events(Result(id.Value, "{}"));
        });
        await using var proxy = new ProxyRun(new HttpClient(server), Loopback, "p");

        await proxy.SendAsync(Request(2, "tools/call", """{"name":"grep","arguments":{}}"""));
        await proxy.SendAsync(Request(3, "tools/list"));

        Assert.Equal(3, (int)(await proxy.ReadAsync())["id"]!);
        Assert.Equal(2, (int)(await proxy.ReadAsync())["id"]!);
    }

    [Fact]
    public async Task An_unknown_project_is_answered_with_an_error_naming_its_slug()
    {
        await using var proxy = Proxy("no-such-project");

        await proxy.SendAsync(Initialize(1));
        var answer = await proxy.ReadAsync();

        Assert.Equal(1, (int)answer["id"]!);
        Assert.Contains("no project with the slug 'no-such-project'", (string?)answer["error"]!["message"]);
    }

    [Fact]
    public async Task A_server_that_is_not_running_is_answered_with_an_error_and_the_proxy_keeps_running()
    {
        using var http = new HttpClient();
        await using var proxy = new ProxyRun(http, new Uri($"http://127.0.0.1:{ClosedPort()}/"), "p");

        await proxy.SendAsync(Initialize(1));
        var first = await proxy.ReadAsync();
        await proxy.SendAsync(Request(2, "tools/list"));
        var second = await proxy.ReadAsync();

        Assert.Contains("CodeExplorer server is not running", (string?)first["error"]!["message"]);
        Assert.Contains("http://127.0.0.1:", (string?)first["error"]!["message"]);
        Assert.Equal(2, (int)second["id"]!);
        Assert.Contains("not running", (string?)second["error"]!["message"]);
        Assert.False(proxy.Completion.IsCompleted);
        Assert.Contains("not running", proxy.Diagnostics.ToString());
        Assert.Empty(await proxy.CloseAsync());
    }

    [Fact]
    public async Task Closing_stdin_ends_the_proxy()
    {
        await using var proxy = Proxy("anything");

        Assert.Empty(await proxy.CloseAsync());
        Assert.True(proxy.Completion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("http://0.0.0.0:5000/")]
    [InlineData("http://192.168.1.10:5000/")]
    [InlineData("http://127.0.0.1.example.com/")]
    public void An_endpoint_off_this_machine_is_refused(string address)
    {
        using var http = new HttpClient();
        var refusal = Assert.Throws<ArgumentException>(() =>
            new StdioProxy(Stream.Null, Stream.Null, http, new Uri(address), "p", null, TextWriter.Null));
        Assert.Contains("not a loopback address", refusal.Message);
    }

    [Fact]
    public async Task A_line_that_is_not_json_is_answered_with_a_parse_error()
    {
        await using var proxy = Proxy("anything");

        await proxy.SendAsync("this is not json");
        var answer = await proxy.ReadAsync();

        Assert.Equal(-32700, (int)answer["error"]!["code"]!);
    }

    /// <summary>
    ///     The SDK's own client, as a stand-in for Claude Desktop, on both eras of the protocol: left to
    ///     itself it probes with 2026-07-28's <c>server/discover</c> and per-request headers, and pinned
    ///     it runs the initialize handshake. Two calls at once, because that is how a client uses it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    public async Task The_SDK_client_lists_and_calls_tools_through_the_proxy(string? protocolVersion)
    {
        await _host.CreateProjectAsync("sdk");
        await using var proxy = Proxy("sdk");

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(proxy.ClientInput, proxy.ClientOutput),
            new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: Ct);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        string[] answers = await Task.WhenAll(
            TestHost.CallAsync(client, "which_project", []),
            TestHost.CallAsync(client, "which_project", []));

        Assert.Contains(tools, tool => tool.Name == "grep");
        Assert.All(answers, answer => Assert.Contains("slug: sdk", answer));
    }

    [Fact]
    public async Task The_session_the_server_hands_out_is_sent_on_every_request_and_ended_on_close()
    {
        using var server = new ScriptedServer((request, body) => Task.FromResult(
            (string?)body?["method"] switch
            {
                "initialize" => ScriptedServer.Events(Result(1, """{"protocolVersion":"2025-06-18"}"""), "session-1"),
                "tools/list" => ScriptedServer.Events(Result(2, """{"tools":[]}""")),
                null when request.Method == HttpMethod.Delete => new HttpResponseMessage(HttpStatusCode.OK),
                _ => ScriptedServer.Accepted(),
            }));
        await using var proxy = new ProxyRun(new HttpClient(server), Loopback, "p");

        await proxy.SendAsync(Initialize(1, "2025-06-18"));
        _ = await proxy.ReadAsync();
        await proxy.SendAsync(Initialized);
        await proxy.SendAsync(Request(2, "tools/list"));
        _ = await proxy.ReadAsync();
        Assert.Empty(await proxy.CloseAsync());

        var received = server.Received.ToList();
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "DELETE"],
            received.Select(r => r.Method));
        Assert.Null(received[0].Session);
        Assert.Null(received[0].ProtocolVersion);
        Assert.All(received.Skip(1), r => Assert.Equal("session-1", r.Session));
        Assert.All(received.Skip(1), r => Assert.Equal("2025-06-18", r.ProtocolVersion));
    }

    [Fact]
    public async Task An_event_whose_data_spans_lines_reaches_the_client_as_one_line()
    {
        using var server = new ScriptedServer((_, _) => Task.FromResult(ScriptedServer.Events(
            "{\"jsonrpc\":\"2.0\",\n\"id\":1,\n\"result\":{\"protocolVersion\":\"2025-11-25\"}}")));
        await using var proxy = new ProxyRun(new HttpClient(server), Loopback, "p");

        await proxy.SendAsync(Initialize(1));
        var answer = await proxy.ReadAsync();

        Assert.Equal("2025-11-25", (string?)answer["result"]!["protocolVersion"]);
        Assert.Empty(await proxy.CloseAsync());
    }

    /// <summary>
    ///     A server that restarted, or a stateful one whose idle timeout passed, answers the old session
    ///     with a 404. The proxy replays the client's initialize for a new one and sends the request
    ///     again; the replayed initialize's own result is the proxy's and must not reach the client,
    ///     which would read it as an answer to nothing.
    /// </summary>
    [Fact]
    public async Task A_session_the_server_lost_is_renewed_and_the_request_sent_again()
    {
        int initializes = 0;
        using var server = new ScriptedServer((request, body) =>
        {
            string? session = request.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null;
            return Task.FromResult((string?)body?["method"] switch
            {
                "initialize" => ScriptedServer.Events(
                    Result(body!["id"]!.ToJsonString(), """{"protocolVersion":"2025-11-25"}"""),
                    $"session-{++initializes}"),
                "tools/list" when session == "session-1" => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"jsonrpc":"2.0","id":2,"error":{"code":-32001,"message":"Session not found"}}""",
                        Encoding.UTF8, "application/json"),
                },
                "tools/list" => ScriptedServer.Events(Result(2, """{"tools":[]}""")),
                _ => ScriptedServer.Accepted(),
            });
        });
        await using var proxy = new ProxyRun(new HttpClient(server), Loopback, "p");

        await proxy.SendAsync(Initialize(1));
        _ = await proxy.ReadAsync();
        await proxy.SendAsync(Initialized);
        await proxy.SendAsync(Request(2, "tools/list"));
        var answer = await proxy.ReadAsync();
        var rest = await proxy.CloseAsync();

        Assert.NotNull(answer["result"]!["tools"]);
        Assert.Empty(rest);
        Assert.Equal(2, initializes);
        Assert.Equal("session-2", server.Received.Last(r => r.Method == "tools/list").Session);
    }

    /// <summary>
    ///     The package's settings file is the server's, so the proxy has to read it as the server does:
    ///     sections, case-insensitive keys, a comment, and relative paths against the <c>app</c> folder.
    /// </summary>
    [Fact]
    public void The_package_settings_name_the_server_the_proxy_starts()
    {
        string app = Path.GetDirectoryName(_host.ScratchFile("app/x"))!;
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, LocalServer.SettingsFile), """
            {
              // The tester moved the port.
              "urls": "http://127.0.0.1:5123",
              "Storage": { "DataDirectory": "..\\data" },
            }
            """);

        using var server = LocalServer.FromPackage(app);

        Assert.NotNull(server);
        Assert.Equal(new Uri("http://127.0.0.1:5123/"), server.BaseAddress);
    }

    [Fact]
    public void A_folder_without_the_settings_file_is_no_package()
    {
        string folder = Path.GetDirectoryName(_host.ScratchFile("empty/x"))!;
        Directory.CreateDirectory(folder);

        Assert.Null(LocalServer.FromPackage(folder));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://192.168.1.10:5000")]
    public void A_settings_file_that_exposes_the_server_is_refused(string urls)
    {
        string app = Path.GetDirectoryName(_host.ScratchFile("exposed/x"))!;
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, LocalServer.SettingsFile), $$"""{ "Urls": "{{urls}}" }""");

        var refusal = Assert.Throws<InvalidOperationException>(() => LocalServer.FromPackage(app));
        Assert.Contains("must listen on this machine only", refusal.Message);
    }

    /// <summary>
    ///     The proxy is the base class library and nothing else: what keeps its stdout the protocol's
    ///     alone is that no logger, host or SDK is there to write to it, and what keeps the single file
    ///     trimmable is that no package brings reflection in.
    /// </summary>
    [Fact]
    public void The_proxy_references_no_package_and_no_project()
    {
        string project = File.ReadAllText(Path.Combine(SourceTree.Server(), "..", "CodeExplorer.McpProxy",
            "CodeExplorer.McpProxy.csproj"));

        Assert.DoesNotContain("PackageReference", project);
        Assert.DoesNotContain("ProjectReference", project);
    }

    private ProxyRun Proxy(string project)
    {
        var http = _host.CreateClient();
        return new ProxyRun(http, http.BaseAddress!, project);
    }

    private static readonly Uri Loopback = new("http://127.0.0.1:5000/");

    private static string Initialize(int id, string version = "2025-11-25") =>
        Request(id, "initialize",
            """{"protocolVersion":""" + $"\"{version}\""
            + ""","capabilities":{},"clientInfo":{"name":"proxy-test","version":"1"}}""");

    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static string Request(int id, string method, string parameters = "{}") =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";

    private static string Result(int id, string result) => Result(id.ToString(System.Globalization.CultureInfo.InvariantCulture), result);

    private static string Result(string id, string result) => $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""";

    /// <summary>A port nothing listens on: one the system handed out and that was closed again at once.</summary>
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    ///     A proxy running over two in-memory pipes, as a client process would see it: what is written to
    ///     <see cref="ClientInput" /> is the proxy's stdin, and <see cref="ClientOutput" /> is its stdout.
    /// </summary>
    private sealed class ProxyRun : IAsyncDisposable
    {
        private readonly Pipe _toProxy = new();
        private readonly Pipe _fromProxy = new();
        private readonly StdioProxy _proxy;
        private readonly HttpClient _http;
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private bool _closed;

        public ProxyRun(HttpClient http, Uri address, string project)
        {
            _http = http;
            _proxy = new StdioProxy(_toProxy.Reader.AsStream(), _fromProxy.Writer.AsStream(), http, address, project,
                null, Diagnostics);
            ClientInput = _toProxy.Writer.AsStream();
            ClientOutput = _fromProxy.Reader.AsStream();
            _writer = new StreamWriter(ClientInput, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _reader = new StreamReader(ClientOutput, Encoding.UTF8);
            Completion = Task.Run(async () =>
            {
                try
                {
                    await _proxy.RunAsync(Ct);
                }
                finally
                {
                    // The end of the proxy is the end of its stdout, which is how the reader learns of it.
                    await _fromProxy.Writer.CompleteAsync();
                }
            }, Ct);
        }

        public Stream ClientInput { get; }

        public Stream ClientOutput { get; }

        public StringWriter Diagnostics { get; } = new();

        public Task Completion { get; }

        public Task SendAsync(string line) => _writer.WriteLineAsync(line);

        public async Task<JsonObject> ReadAsync()
        {
            string? line = await _reader.ReadLineAsync(Ct).AsTask().WaitAsync(Patience, Ct);
            Assert.NotNull(line);
            return Assert.IsType<JsonObject>(JsonNode.Parse(line));
        }

        /// <summary>Closes stdin, waits for the proxy to end, and answers every line it wrote after the last read.</summary>
        public async Task<List<string>> CloseAsync()
        {
            _closed = true;
            await _toProxy.Writer.CompleteAsync();
            await Completion.WaitAsync(Patience, Ct);
            var rest = new List<string>();
            while (await _reader.ReadLineAsync(Ct) is { } line) rest.Add(line);
            return rest;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_closed) await _toProxy.Writer.CompleteAsync();
            await Completion.WaitAsync(Patience, Ct);
            _proxy.Dispose();
            _http.Dispose();
            await _writer.DisposeAsync();
            _reader.Dispose();
        }
    }

    /// <summary>A server reduced to an answer per request, recording what each request carried.</summary>
    private sealed class ScriptedServer(Func<HttpRequestMessage, JsonObject?, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        public ConcurrentQueue<Received> Received { get; } = new();

        public static HttpResponseMessage Events(string message, string? session = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"event: message\n{string.Join("\n", message.Split('\n').Select(line => "data: " + line))}\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
            if (session is not null) response.Headers.Add("Mcp-Session-Id", session);
            return response;
        }

        public static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            Received.Enqueue(new Received(
                (string?)body?["method"] ?? request.Method.Method,
                Header(request, "Mcp-Session-Id"),
                Header(request, "MCP-Protocol-Version")));
            return await answer(request, body);
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }

    private sealed record Received(string Method, string? Session, string? ProtocolVersion);
}
