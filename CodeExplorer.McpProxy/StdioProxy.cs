using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeExplorer.McpProxy;

/// <summary>
///     Forwards MCP between a client that speaks only the stdio transport and one project's Streamable
///     HTTP endpoint on this machine. It exists for Claude Desktop, whose configuration file accepts only
///     a command to start and whose custom connectors are brokered from Anthropic's cloud and cannot reach
///     a loopback address, so the evaluation package (docs/deployment/local-evaluation.md) has no other
///     way to put a local server in front of it.
///     It forwards and does not interpret: every line read is POSTed as it is, and every message the
///     server answers with is written back as one line, so the protocol versions, the tools and their
///     errors are the server's and not a second copy kept here. What it adds is only what the two
///     transports disagree on — the session and protocol-version headers, the event stream, and the
///     failures a pipe has no status code for, which become JSON-RPC errors for the request that met them.
///     There is no GET stream for server-initiated messages: CodeExplorer's tools are fixed at startup,
///     and nothing in it sends progress, logging, sampling, elicitation or a list change, so everything it
///     ever sends is the answer to a POST and arrives on that POST's own stream. A GET would hold a
///     connection open for messages that never come — and on the stateless transport this server runs,
///     the SDK has no session to hang one on.
/// </summary>
public sealed class StdioProxy : IDisposable
{
    /// <summary>
    ///     A JSON-RPC error code in the range the specification reserves for implementation-defined
    ///     server errors, for the failures that are the proxy's own: a server that is not running, a
    ///     project that does not exist, an HTTP answer that carried no JSON-RPC error of its own. A client
    ///     shows the message; nothing branches on the number.
    /// </summary>
    private const int ProxyErrorCode = -32000;

    /// <summary>The <c>_meta</c> key a 2026-07-28 request names its protocol version under (SEP-2575).</summary>
    private const string ProtocolVersionMeta = "io.modelcontextprotocol/protocolVersion";

    /// <summary>
    ///     How long the session's DELETE may take when the client has gone. The session expires on the
    ///     server's idle timeout anyway, so a server too slow to answer is not worth holding an exiting
    ///     process open for.
    /// </summary>
    private static readonly TimeSpan EndSessionTimeout = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    ///     Relaxed, because what reads this is a JSON parser and never an HTML page: the default encoder
    ///     escapes every non-ASCII character, which would turn a German comment in a tool's answer into
    ///     a run of escape sequences for no reader's benefit.
    /// </summary>
    private static readonly JsonSerializerOptions LineFormat = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _project;
    private readonly LocalServer? _server;
    private readonly TextWriter _diagnostics;

    /// <summary>One line at a time on stdout, so two answers finishing together never interleave.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Held while a lost session is replaced, so concurrent requests that all met it renew it once.</summary>
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private string? _sessionId;
    private string? _protocolVersion;

    /// <summary>
    ///     The client's own initialize request, kept to replay it when the server has lost the session —
    ///     a restart, or the SDK's idle timeout on a stateful server. Claude Desktop keeps a proxy for
    ///     days and would otherwise answer every later call with an error until it is restarted.
    /// </summary>
    private JsonObject? _initialize;

    /// <param name="input">What the client writes: one JSON-RPC message per line, UTF-8.</param>
    /// <param name="output">What the client reads, which carries protocol messages and nothing else.</param>
    /// <param name="http">The client the endpoint is reached through. Its timeout bounds a whole tool call.</param>
    /// <param name="address">The server's base address, which must be a loopback address.</param>
    /// <param name="project">The slug of the project whose MCP endpoint the client is connected to.</param>
    /// <param name="server">
    ///     The evaluation package's server, started when nothing answers. Null where the proxy runs
    ///     beside a server it does not manage, such as a developer's <c>dotnet run</c>: a server it did
    ///     not start, it does not start either.
    /// </param>
    /// <param name="diagnostics">Standard error, where a client logs what a server process says about itself.</param>
    public StdioProxy(Stream input, Stream output, HttpClient http, Uri address, string project,
        LocalServer? server, TextWriter diagnostics)
    {
        var endpoint = Endpoint(address, project);
        if (!IsLoopback(endpoint))
            throw new ArgumentException(
                $"The proxy connects only to a server on this machine, and {endpoint} is not a loopback address. "
                + "CodeExplorer without authentication must not be reached over a network.", nameof(address));

        _input = input;
        _output = output;
        _http = http;
        _endpoint = endpoint;
        _project = project;
        _server = server;
        _diagnostics = TextWriter.Synchronized(diagnostics);
    }

    /// <summary>
    ///     Loopback by the address the URL names, which is what the server's own host filter accepts when
    ///     authentication is off: <c>localhost</c>, 127.0.0.0/8 and <c>::1</c>. A wildcard such as
    ///     <c>0.0.0.0</c> is refused with the rest, because it is what a server listening on every
    ///     interface is configured with, and a proxy pointed there is a sign the server is exposed.
    /// </summary>
    public static bool IsLoopback(Uri endpoint) =>
        endpoint.IsAbsoluteUri && endpoint.Scheme is "http" or "https" && endpoint.IsLoopback;

    /// <summary>The MCP endpoint of one project on a server (ADR-0002).</summary>
    public static Uri Endpoint(Uri server, string project) =>
        new(server, $"projects/{Uri.EscapeDataString(project)}/mcp");

    /// <summary>
    ///     Forwards until the client closes stdin, then lets every request already sent finish, ends the
    ///     session and returns. Draining rather than cancelling, because a client that pipes a script of
    ///     requests in closes its end right after the last one and still reads the answers.
    ///     Only <c>initialize</c> is awaited before the next line is read: everything after it depends on
    ///     the session and the version it negotiates, and a client may send nothing but a ping before it
    ///     has the answer anyway. Notifications and responses are awaited too, which is quick — the server
    ///     answers them with a bare 202 — and keeps <c>notifications/initialized</c> ahead of the requests
    ///     behind it. Every other request runs on its own, so one slow tool call does not hold up a list.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pending = new List<Task>();
        using var reader = new StreamReader(_input, Utf8, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (Parse(line) is not { } message)
            {
                await WriteAsync(ErrorReply(null, -32700,
                    "The proxy read a line that is not a JSON-RPC message. Each line must be one JSON object."),
                    cancellationToken);
                continue;
            }

            var forwarding = ForwardAsync(message, cancellationToken);
            if (IsRequest(message) && MethodOf(message) != "initialize") pending.Add(forwarding);
            else await forwarding;
            pending.RemoveAll(task => task.IsCompleted);
        }

        await Task.WhenAll(pending);
        await EndSessionAsync(cancellationToken);
    }

    /// <summary>
    ///     One message, with whatever went wrong answered on the request's id rather than thrown: an
    ///     exception here would end the proxy and Claude Desktop would show a server that crashed instead
    ///     of the sentence saying what to do. A notification or a response has no id to answer on, so its
    ///     failure goes to standard error alone.
    /// </summary>
    private async Task ForwardAsync(JsonObject message, CancellationToken cancellationToken)
    {
        string? method = MethodOf(message);
        string? failure;
        try
        {
            failure = await SendAsync(message, method, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Safe to swallow: it is answered on the request below and reported in full on standard
            // error, which is where a client keeps a server's log.
            failure = $"The proxy could not forward {method ?? "a response"} to CodeExplorer: {ex.Message}";
            await _diagnostics.WriteLineAsync($"codeexplorer proxy: {ex}");
        }

        if (failure is null) return;
        await _diagnostics.WriteLineAsync($"codeexplorer proxy: {method ?? "response"}: {failure}");
        if (IsRequest(message))
            await WriteAsync(ErrorReply(message["id"]?.DeepClone(), ProxyErrorCode, failure), cancellationToken);
    }

    /// <summary>
    ///     POSTs the message and relays the answer. Null when it was relayed — an error the server
    ///     answered as JSON-RPC included, because that is the server's answer and not a failure of the
    ///     proxy — and otherwise the sentence the request is answered with.
    ///     Each recovery is tried once per message: starting the server when nothing listens, and
    ///     renewing a session the server no longer knows. Once is what tells a stopped server from one
    ///     that cannot start, and a session renewed and lost again is a server with a problem of its own.
    /// </summary>
    private async Task<string?> SendAsync(JsonObject message, string? method, CancellationToken cancellationToken)
    {
        bool started = false;
        bool renewed = false;
        while (true)
        {
            string? session = method == "initialize" ? null : _sessionId;
            HttpResponseMessage response;
            try
            {
                response = await PostAsync(message, method, session, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
            {
                if (_server is null || started) return NotRunning(ex.Message);
                started = true;
                if (await _server.EnsureRunningAsync(_http, cancellationToken) is { } why) return NotRunning(why);
                // A server that was not answering has forgotten every session it handed out before.
                if (session is not null && await RenewSessionAsync(session, cancellationToken) is { } lost)
                    return lost;
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound && session is not null && !renewed
                    && IsSessionNotFound(await PeekAsync(response, cancellationToken)))
                {
                    renewed = true;
                    if (await RenewSessionAsync(session, cancellationToken) is { } lost) return lost;
                    continue;
                }

                if (!response.IsSuccessStatusCode) return await RefusalAsync(message, response, cancellationToken);

                if (method == "initialize")
                {
                    _initialize = (JsonObject)message.DeepClone();
                    _sessionId = SessionOf(response);
                }

                await RelayAsync(response, method == "initialize", cancellationToken);
                return null;
            }
        }
    }

    private async Task<HttpResponseMessage> PostAsync(JsonObject message, string? method, string? session,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(message.ToJsonString(LineFormat), Utf8, "application/json"),
        };
        // Both, or the SDK refuses the POST with a 406: it decides per message which of the two it answers in.
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);

        // A 2026-07-28 request names its own version; a handshake client's is the one initialize
        // negotiated, and initialize itself carries none, which is what keeps the SDK on the handshake.
        string? version = MetaVersionOf(message) ?? (method == "initialize" ? null : _protocolVersion);
        if (version is not null) request.Headers.Add("MCP-Protocol-Version", version);

        // The standard request headers 2026-07-28 makes required. The SDK checks them only on that
        // version and ignores them on every earlier one, so they are sent whatever the version is.
        // Mcp-Param-* is left out: it is for arguments a tool's schema marks x-mcp-header, and no
        // CodeExplorer tool marks one.
        if (method is not null) request.Headers.Add("Mcp-Method", method);
        if (RoutingNameOf(message, method) is { } name) request.Headers.Add("Mcp-Name", HeaderValue(name));

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>
    ///     Writes every message of the answer, in order. The SDK answers a request on an event stream
    ///     and a notification with a bare 202; a plain JSON body is relayed as well, because the
    ///     specification lets a server choose either for a request.
    /// </summary>
    private async Task RelayAsync(HttpResponseMessage response, bool negotiates, CancellationToken cancellationToken)
    {
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (type == "text/event-stream")
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Utf8);
            var data = new StringBuilder();
            bool hasData = false;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    if (hasData) await RelayMessageAsync(data.ToString(), negotiates, cancellationToken);
                    data.Clear();
                    hasData = false;
                    continue;
                }

                // An event's data may span several lines, joined with a line feed; event names, ids and
                // retry hints mean nothing on a pipe, and a line starting with a colon is a comment.
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                if (hasData) data.Append('\n');
                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                hasData = true;
            }

            // A stream cut off without its closing blank line still delivered what it sent.
            if (hasData) await RelayMessageAsync(data.ToString(), negotiates, cancellationToken);
            return;
        }

        if (type == "application/json")
            await RelayMessageAsync(await response.Content.ReadAsStringAsync(cancellationToken), negotiates,
                cancellationToken);
    }

    /// <summary>
    ///     Re-serialised rather than copied, so a message the server wrote across several lines still
    ///     reaches the pipe as one: on stdio a line break is the end of a message.
    /// </summary>
    private async Task RelayMessageAsync(string payload, bool negotiates, CancellationToken cancellationToken)
    {
        if (Parse(payload) is not { } message)
        {
            await _diagnostics.WriteLineAsync("codeexplorer proxy: the server sent an event that is not a JSON-RPC message; it was dropped.");
            return;
        }

        if (negotiates && TextOf(message["result"]?["protocolVersion"]) is { } negotiated)
            _protocolVersion = negotiated;

        await WriteAsync(message, cancellationToken);
    }

    /// <summary>
    ///     Replays the client's initialize under an id of the proxy's own, keeps the new session and
    ///     sends <c>notifications/initialized</c> for it; the reply is the proxy's and is not relayed,
    ///     because the client finished its handshake long ago and would read a second result as a reply
    ///     to nothing. Null when the session was renewed, here or by a request that met the loss first.
    /// </summary>
    private async Task<string?> RenewSessionAsync(string lost, CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_sessionId != lost) return null;
            if (_initialize?.DeepClone() is not JsonObject replay)
                return "CodeExplorer no longer knows this session and the proxy has no initialize to renew it with. Restart the client.";

            replay["id"] = $"codeexplorer-proxy-{Guid.NewGuid():N}";
            using (var response = await PostAsync(replay, "initialize", null, cancellationToken))
            {
                if (!response.IsSuccessStatusCode) return await RefusalAsync(replay, response, cancellationToken);
                _sessionId = SessionOf(response);
                // Read to the end, so the server's stream is finished rather than abandoned.
                _ = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            var initialized = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
            using (await PostAsync(initialized, "notifications/initialized", _sessionId, cancellationToken))
                await _diagnostics.WriteLineAsync("codeexplorer proxy: the server had lost the session; it was renewed.");
            return null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    ///     Says goodbye to a stateful server, which the specification asks of a client that is done
    ///     with a session. A stateless server handed out no session and is told nothing.
    /// </summary>
    private async Task EndSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is not { } session) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EndSessionTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint);
            request.Headers.Add("Mcp-Session-Id", session);
            if (_protocolVersion is not null) request.Headers.Add("MCP-Protocol-Version", _protocolVersion);
            using var answer = await _http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Safe to swallow: the client has gone, and a session the server was not told about ends
            // on its idle timeout anyway.
        }
    }

    /// <summary>
    ///     The sentence for an HTTP answer that is not a success. An error the server wrote as JSON-RPC is
    ///     relayed as it is, on the request's id, so that its code and data reach the client — a client
    ///     falls back from one protocol version to another on exactly those. Everything else is described
    ///     in words, a project that does not exist above all, because that 404 is the one an evaluator
    ///     meets after a typo in the configuration file.
    /// </summary>
    private async Task<string?> RefusalAsync(JsonObject message, HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await PeekAsync(response, cancellationToken);
        var answer = Parse(body);
        if (answer?["error"] is JsonObject detail)
        {
            if (!IsRequest(message)) return $"CodeExplorer refused it: {detail["message"]}";
            var reply = ErrorReply(message["id"]?.DeepClone(), ProxyErrorCode, string.Empty);
            reply["error"] = detail.DeepClone();
            await WriteAsync(reply, cancellationToken);
            return null;
        }

        string? said = TextOf(answer?["error"]);
        if (response.StatusCode == HttpStatusCode.NotFound && said is not null)
            return $"CodeExplorer has no project with the slug '{_project}'. Create the project in the CodeExplorer "
                   + $"web UI at {new Uri(_endpoint, "/")}, or correct the slug given to CodeExplorer.McpProxy in the "
                   + "client's configuration, then restart the client.";

        return $"CodeExplorer answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
               + (said is null ? "." : $": {said}");
    }

    /// <summary>
    ///     The sentence for a server nobody answers for. In a package it names <c>start.cmd</c>, which
    ///     shows the server's console and so why it would not start; outside one the server is someone's
    ///     <c>dotnet run</c>, and there is no script to name.
    /// </summary>
    private string NotRunning(string why) =>
        $"The CodeExplorer server is not running at {new Uri(_endpoint, "/")} ({why.TrimEnd('.')}). "
        + (_server is null
            ? "Start it, then ask again."
            : "Start it with start.cmd in the CodeExplorer folder, then ask again.");

    /// <summary>The body, read once and buffered, so the decision about it and the reply to it see the same text.</summary>
    private static async Task<string> PeekAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>The SDK's answer to a session id it does not hold: a 404 carrying JSON-RPC error -32001.</summary>
    private static bool IsSessionNotFound(string body) =>
        Parse(body)?["error"]?["code"] is JsonValue code && code.TryGetValue(out int value) && value == -32001;

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        byte[] line = Utf8.GetBytes(message.ToJsonString(LineFormat) + "\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(line, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // Safe to swallow: the client closed its end of the pipe, so nobody is reading any more, and
            // the stdin it closes with it is what ends the proxy.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static JsonObject ErrorReply(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>A JSON object, or null for anything else: a batch, a bare value or no JSON at all.</summary>
    private static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            // Safe to swallow: null is the answer for text that is not a message, and each caller says so.
            return null;
        }
    }

    private static bool IsRequest(JsonObject message) => message.ContainsKey("method") && message.ContainsKey("id");

    private static string? MethodOf(JsonObject message) => TextOf(message["method"]);

    private static string? MetaVersionOf(JsonObject message) => TextOf(message["params"]?["_meta"]?[ProtocolVersionMeta]);

    /// <summary>A JSON string's text, or null for anything else, a missing member included.</summary>
    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>The parameter the SDK routes these methods by, which 2026-07-28 repeats in <c>Mcp-Name</c>.</summary>
    private static string? RoutingNameOf(JsonObject message, string? method)
    {
        string? parameter = method switch
        {
            "tools/call" or "prompts/get" => "name",
            "resources/read" => "uri",
            _ => null,
        };
        return parameter is null ? null : TextOf(message["params"]?[parameter]);
    }

    /// <summary>
    ///     A name as SEP-2243 puts it in a header: as it is when it is printable ASCII with no space at
    ///     either end, and otherwise as <c>=?base64?…?=</c> over its UTF-8, which is also how a name that
    ///     happens to look like that wrapper is sent. Spelled out here rather than taken from the SDK,
    ///     which this program does not reference; every CodeExplorer tool name takes the first branch.
    /// </summary>
    private static string HeaderValue(string name)
    {
        const string prefix = "=?base64?";
        const string suffix = "?=";
        bool plain = (name.Length == 0 || (name[0] != ' ' && name[^1] != ' '))
                     && name.All(c => c is >= ' ' and <= '~')
                     && !(name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal));
        return plain ? name : prefix + Convert.ToBase64String(Utf8.GetBytes(name)) + suffix;
    }

    private static string? SessionOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;

    public void Dispose()
    {
        _writeLock.Dispose();
        _sessionLock.Dispose();
    }

}
