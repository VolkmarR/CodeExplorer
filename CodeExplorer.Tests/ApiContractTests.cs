using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The names of every JSON property every GET route under <c>/api</c> answers with, compared with
///     <c>ApiContract.txt</c> beside this file. The web UI declares each response shape by hand, and
///     every other suite reads an answer back into the record the server wrote it from, so a renamed or
///     removed property passes there and leaves the web reading <c>undefined</c>. Here the answer is
///     read as text and only its property paths are kept — values never, so a snapshot changes only
///     when a shape does.
///     <para>
///         The routes come from the server's own endpoint table, so a new GET route fails this test
///         until it has a request in <see cref="Requests" /> and lines in the snapshot, or a reason in
///         <see cref="Excluded" />.
///     </para>
///     <para>
///         To accept a change, run this test with <c>CODEEXPLORER_UPDATE_API_CONTRACT=1</c> set: it
///         rewrites the snapshot from the answers and passes. Nothing else writes it, so a normal run
///         can only compare. A snapshot change in a pull request changes the web type that mirrors the
///         route in the same pull request (CODING_STANDARDS, TypeScript).
///     </para>
///     <para>
///         Beside <see cref="HttpJsonTests" /> and on its fixture, not in place of it: that class pins
///         the values the web compares against — each enum's spelling — which a names-only snapshot
///         cannot see. The property-name lists those tests held for a few answers moved here, where
///         every answer has one.
///     </para>
/// </summary>
public sealed class ApiContractTests(HttpJsonFixture fixture) : IClassFixture<HttpJsonFixture>
{
    /// <summary>The switch that rewrites the snapshot. An environment variable, so no code changes to set it.</summary>
    public const string UpdateSwitch = "CODEEXPLORER_UPDATE_API_CONTRACT";

    private const string SnapshotFile = "ApiContract.txt";

    private readonly TestHost _host = fixture.Host;

    /// <summary>
    ///     The request each route is read through, by its route pattern as the endpoint table spells it.
    ///     Each one is chosen so that the answer is not empty: an empty array or a null object has no
    ///     property paths below it, and a shape nobody sees cannot be compared. <c>{sha}</c> is the
    ///     newest commit of the fixture's project, which only the change log can say.
    /// </summary>
    private static readonly Dictionary<string, string> Requests = new(StringComparer.Ordinal)
    {
        ["/api/auth/me"] = "/api/auth/me",
        ["/api/projects"] = "/api/projects",
        ["/api/tool-calls"] = "/api/tool-calls",
        ["/api/projects/{project}"] = Project(""),
        ["/api/projects/{project}/tool-calls"] = Project("/tool-calls"),
        ["/api/projects/{project}/overview"] = Project("/overview"),
        ["/api/projects/{project}/excluded-paths"] = Project("/excluded-paths"),
        ["/api/projects/{project}/excluded-paths/suggestions"] = Project("/excluded-paths/suggestions"),
        ["/api/projects/{project}/repositories"] = Project("/repositories"),
        ["/api/projects/{project}/refresh"] = Project("/refresh"),
        ["/api/projects/{project}/search"] = Project("/search?q=class"),
        ["/api/projects/{project}/files"] = Project("/files"),
        ["/api/projects/{project}/tree"] = Project("/tree?path=one"),
        ["/api/projects/{project}/file"] = Project("/file?path=one/src/Orders.cs"),
        ["/api/projects/{project}/file/blame"] = Project("/file/blame?path=one/src/Orders.cs"),
        ["/api/projects/{project}/file/imports"] = Project("/file/imports?path=one/src/Orders.cs"),
        ["/api/projects/{project}/file/declarations"] = Project("/file/declarations?path=one/src/Customers.pas"),
        ["/api/projects/{project}/commits"] = Project("/commits"),
        ["/api/projects/{project}/churn"] = Project("/churn"),
        ["/api/projects/{project}/commits/{sha}"] = Project("/commits/{sha}"),
        ["/api/projects/{project}/commits/{sha}/files"] = Project("/commits/{sha}/files")
    };

    /// <summary>
    ///     GET routes under <c>/api</c> this test does not read, by pattern, each with the reason. Empty
    ///     today. <c>/api/auth/signin</c> is not here because this host never maps it: it exists only
    ///     with a tenant configured, and it answers with a redirect to the tenant rather than JSON.
    /// </summary>
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal);

    private static string Project(string rest) => $"/api/projects/{HttpJsonFixture.Built}{rest}";

    [Fact]
    public async Task Every_api_read_answers_with_the_property_names_in_the_snapshot()
    {
        var routes = GetRoutes();
        Assert.Empty(routes.Where(route => !Requests.ContainsKey(route) && !Excluded.ContainsKey(route))
            .Select(route => $"{route} has no request in {nameof(Requests)} and no reason in {nameof(Excluded)}."));
        Assert.Empty(Requests.Keys.Concat(Excluded.Keys).Where(route => !routes.Contains(route))
            .Select(route => $"{route} is listed here, but the server maps no GET route by that pattern."));

        string sha = await NewestCommitAsync();
        var actual = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach ((string route, string request) in Requests)
            actual[route] = PropertyPaths(await ReadAsync(route, request.Replace("{sha}", sha)));

        string path = Path.Combine(SourceTree.Tests(), SnapshotFile);
        if (Environment.GetEnvironmentVariable(UpdateSwitch) == "1")
            await File.WriteAllTextAsync(path, Write(actual), TestContext.Current.CancellationToken);

        var expected = File.Exists(path)
            ? Read(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))
            : new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        string differences = Differences(expected, actual);
        Assert.True(differences.Length == 0,
            $"""
             The JSON property names of /api answers differ from {SnapshotFile}:
             {differences}
             If the change is intended, run this test with {UpdateSwitch}=1 to rewrite the snapshot,
             and change the type in web/src/features that mirrors each route above in the same pull request.
             """);
    }

    /// <summary>Every GET route the server maps under <c>/api</c>, by pattern, from its own endpoint table.</summary>
    private HashSet<string> GetRoutes() =>
    [
        .. _host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.Trim('/'))
            .Where(route => route == "/api" || route.StartsWith("/api/", StringComparison.Ordinal))
    ];

    private async Task<string> NewestCommitAsync()
    {
        var log = await _host.GetJsonNodeAsync(Project("/commits"));
        return (string)log["commits"]![0]!["sha"]!;
    }

    private async Task<JsonNode?> ReadAsync(string route, string request)
    {
        using var http = _host.CreateClient();
        using var response = await http.GetAsync(request, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode,
            $"GET {request} (route {route}) answered {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body);
    }

    /// <summary>
    ///     Every property path in <paramref name="answer" />: names joined by dots, an array written as
    ///     <c>[]</c> after its name, and the paths of all its elements merged under it.
    /// </summary>
    private static SortedSet<string> PropertyPaths(JsonNode? answer)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Visit(answer, "");
        return paths;

        void Visit(JsonNode? value, string path)
        {
            if (value is JsonArray array)
            {
                path += "[]";
                paths.Add(path);
                foreach (var element in array) Visit(element, path);
                return;
            }

            if (path.Length > 0) paths.Add(path);
            if (value is not JsonObject properties) return;
            foreach ((string name, var property) in properties)
                Visit(property, path.Length == 0 ? name : $"{path}.{name}");
        }
    }

    /// <summary>
    ///     One line per route, then one per property path prefixed with its route, sorted: a changed path
    ///     shows in a diff with its route on the same line, however long the route's list is.
    /// </summary>
    private static string Write(SortedDictionary<string, SortedSet<string>> snapshot)
    {
        var text = new StringBuilder();
        foreach ((string route, var paths) in snapshot)
        {
            text.Append(route).Append('\n');
            foreach (string path in paths) text.Append(route).Append(' ').Append(path).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The snapshot as <see cref="Write" /> spells it, whichever line ending the checkout gave it.</summary>
    private static SortedDictionary<string, SortedSet<string>> Read(string text)
    {
        var snapshot = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', 2);
            if (!snapshot.TryGetValue(parts[0], out var paths)) snapshot[parts[0]] = paths = new(StringComparer.Ordinal);
            if (parts.Length == 2) paths.Add(parts[1]);
        }

        return snapshot;
    }

    /// <summary>Per route, the paths the snapshot has and the answer lacks (-) and the reverse (+). Empty when they agree.</summary>
    private static string Differences(SortedDictionary<string, SortedSet<string>> expected,
        SortedDictionary<string, SortedSet<string>> actual)
    {
        var lines = new List<string>();
        foreach (string route in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(route, out var now))
                lines.Add($"  {route}: in the snapshot, but no longer read");
            else if (!expected.TryGetValue(route, out var then))
                lines.Add($"  {route}: not in the snapshot");
            else if (!now.SetEquals(then))
                lines.AddRange([
                    $"  {route}:",
                    .. then.Except(now).Select(path => $"    - {path}"),
                    .. now.Except(then).Select(path => $"    + {path}")
                ]);
        }

        return string.Join('\n', lines);
    }
}
