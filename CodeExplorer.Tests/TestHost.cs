using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using LibGit2Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     One in-process server with its own data directory and a pinned search engine, plus the fixture
///     steps every index-backed test repeats: a git repository with one commit, a project, its
///     repositories, a build, and an MCP client bound to the project's route.
/// </summary>
public sealed class TestHost : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    /// <param name="engine">Pinned in every test: the two engines build different schemas and rank differently.</param>
    /// <param name="drainSeconds">
    ///     How long a swap waits for in-flight queries. Zero proves what happens to a connection the
    ///     drain gave up on; the default is long enough that a test holding one blocks the swap.
    /// </param>
    /// <param name="minimumFreeBytes">Raised past any real disk to prove the free-space refusal.</param>
    public TestHost(SearchEngine engine, int? drainSeconds = null, long? minimumFreeBytes = null)
    {
        _engine = engine;
        _drainSeconds = drainSeconds;
        _minimumFreeBytes = minimumFreeBytes;
        Factory = Build();
    }

    private readonly SearchEngine _engine;
    private readonly int? _drainSeconds;
    private readonly long? _minimumFreeBytes;

    public WebApplicationFactory<Program> Factory { get; private set; }

    private WebApplicationFactory<Program> Build() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:DataDirectory", DataDirectory);
            builder.UseSetting("Storage:DurableDirectory", DurableDirectory);
            builder.UseSetting("Index:SearchEngine", _engine.ToString());
            if (_drainSeconds is { } seconds)
                builder.UseSetting("Index:DrainSeconds", seconds.ToString(CultureInfo.InvariantCulture));
            if (_minimumFreeBytes is { } bytes)
                builder.UseSetting("Refresh:MinimumFreeBytes", bytes.ToString(CultureInfo.InvariantCulture));
        });

    /// <summary>
    ///     Stops the server and starts a new one over the same directories, which is what a scale to
    ///     zero and a wake look like from here: the singletons, the attach state and the in-memory
    ///     refresh statuses are all gone, and only what is on disk and in the durable store remains.
    /// </summary>
    public void Restart()
    {
        Factory.Dispose();
        Factory = Build();
    }

    /// <summary>
    ///     Removes a project's index file, which is what the container's disk being wiped leaves behind:
    ///     a project that exists, has a durable copy, and has nothing local to answer from.
    /// </summary>
    public void DeleteIndexFile(string slug) =>
        DeleteDatabase(Path.Combine(DataDirectory, "indexes", slug + ".duckdb"));

    /// <summary>The same for the control database, whose backup is a file rather than a Parquet set.</summary>
    public void DeleteControlDatabase() => DeleteDatabase(Path.Combine(DataDirectory, "control.duckdb"));

    /// <summary>A database file and the write-ahead log beside it, which a stop leaves behind.</summary>
    private static void DeleteDatabase(string path)
    {
        File.Delete(path);
        File.Delete(path + ".wal");
    }

    /// <summary>Where the folder durable store keeps a project's Parquet set, whether or not it has one.</summary>
    public string DurableIndexDirectory(string slug) => Path.Combine(DurableDirectory, "indexes", slug);

    /// <summary>The folder standing in for a blob container, which is what an unconfigured app uses.</summary>
    public string DurableDirectory => Path.Combine(_root, "durable");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Factory.Dispose();
        DeleteTree(_root);
    }

    /// <summary>
    ///     Removes a directory git has written into. libgit2 marks pack files read-only and
    ///     <c>Directory.Delete</c> refuses a read-only file, so the attributes are cleared first rather
    ///     than left to fail on the first pack.
    /// </summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(path, true);
    }

    /// <summary>Builds a non-bare repository with one commit holding the given files and returns its path.</summary>
    public string CreateGitRepository(string name, Dictionary<string, string> files)
    {
        string path = Path.Combine(_root, "fixtures", name);
        Repository.Init(path);
        return Commit(path, files);
    }

    /// <summary>
    ///     Adds a commit to a fixture already created, which is what a push to the remote looks like
    ///     from here: the clone made earlier still holds the old tree until something fetches.
    /// </summary>
    public string CommitToGitRepository(string name, Dictionary<string, string> files) =>
        Commit(Path.Combine(_root, "fixtures", name), files);

    private static string Commit(string path, Dictionary<string, string> files)
    {
        using var repo = new Repository(path);
        foreach ((string relative, string content) in files)
        {
            string full = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Commands.Stage(repo, relative);
        }

        var author = new Signature("Test", "test@example.invalid", DateTimeOffset.UnixEpoch);
        repo.Commit("fixture", author, author);
        return path;
    }

    /// <summary>Where <see cref="CreateGitRepository" /> put the fixture with this name.</summary>
    public string FixturePath(string name) => Path.Combine(_root, "fixtures", name);

    /// <summary>Deletes a fixture, which is what a remote that was removed or renamed looks like from here.</summary>
    public void RemoveGitRepository(string name) => DeleteTree(FixturePath(name));

    /// <summary>What the host was pointed at, for a test asserting which files a deletion left behind.</summary>
    public string DataDirectory => Path.Combine(_root, "data");

    public async Task CreateProjectAsync(string slug, bool singleRepository = false)
    {
        using var http = Factory.CreateClient();
        using var response =
            await http.PostAsJsonAsync("/api/projects", new { slug, name = slug, singleRepository }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public async Task AddRepositoryAsync(string project, string slug, string url, string? credential = null)
    {
        using var http = Factory.CreateClient();
        using var response =
            await http.PostAsJsonAsync($"/api/projects/{project}/repositories", new { slug, url, credential }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    ///     Asks for a refresh and waits for it to finish. The endpoint answers as soon as the work is
    ///     queued, so the wait is on the service's own task rather than on a poll: a test that polled
    ///     would trade determinism for a sleep. <see cref="RefreshStatusAsync" /> covers the polling.
    /// </summary>
    public async Task<IndexSummary> RefreshAsync(string project)
    {
        using (var response = await RequestRefreshAsync(project))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await WaitForRefreshesAsync();
        var status = await RefreshStatusAsync(project);
        Assert.Equal(RefreshState.Succeeded, status.State);
        Assert.NotNull(status.Summary);
        return status.Summary;
    }

    /// <summary>Asks for a refresh and returns the response, for a test that asserts on the refusal.</summary>
    public async Task<HttpResponseMessage> RequestRefreshAsync(string project)
    {
        using var http = Factory.CreateClient();
        return await http.PostAsync($"/api/projects/{project}/refresh", null, Ct);
    }

    /// <summary>The host's refresh service, for a test asserting on the queue rather than on a response.</summary>
    public RefreshService Refreshes => Factory.Services.GetRequiredService<RefreshService>();

    /// <summary>Waits for every refresh asked for so far, including one queued behind another.</summary>
    public Task WaitForRefreshesAsync() => Refreshes.Pending.WaitAsync(Ct);

    /// <summary>
    ///     The host's index store, for a test that reads a project's tables directly rather than
    ///     through an endpoint. Resolved on each use: it is a singleton, so this is the same instance
    ///     the server answers requests from.
    /// </summary>
    public ProjectIndexes Indexes => Factory.Services.GetRequiredService<ProjectIndexes>();

    /// <summary>A lease on a project that is expected to have an index; the caller disposes it.</summary>
    public async Task<IndexLease> OpenIndexAsync(string slug)
    {
        var lease = await Indexes.OpenAsync(slug, Ct);
        Assert.NotNull(lease);
        return lease;
    }

    /// <summary>
    ///     The first column of every row the query returns, read through a lease of its own. Tests
    ///     assert on a list of strings — paths, reasons, schema names — often enough that spelling out
    ///     the reader each time is the whole body of the test.
    /// </summary>
    public async Task<List<string>> ScalarsAsync(string slug, string sql)
    {
        using var lease = await OpenIndexAsync(slug);
        return await ScalarsAsync(lease, sql);
    }

    /// <summary>The same, against a lease already held: a test proving what a swap does to one in flight.</summary>
    public static async Task<List<string>> ScalarsAsync(IndexLease lease, string sql)
    {
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct)) values.Add(reader.GetString(0));
        return values;
    }

    public async Task<RefreshStatus> RefreshStatusAsync(string project)
    {
        using var http = Factory.CreateClient();
        var status = await http.GetFromJsonAsync<RefreshStatus>($"/api/projects/{project}/refresh", Ct);
        Assert.NotNull(status);
        return status;
    }

    /// <summary>A project of the given repositories (slug to files), created, added and indexed.</summary>
    public async Task<IndexSummary> IndexedProjectAsync(string project,
        Dictionary<string, Dictionary<string, string>> repositories, bool singleRepository = false)
    {
        await CreateProjectAsync(project, singleRepository);
        foreach ((string slug, var files) in repositories)
            await AddRepositoryAsync(project, slug, CreateGitRepository(slug, files));
        return await RefreshAsync(project);
    }

    public async Task<McpClient> ConnectAsync(string slug)
    {
        var http = Factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, $"/projects/{slug}/mcp") },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>Calls a tool and returns its single text block.</summary>
    public static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        var block = Assert.Single(result.Content);
        return Assert.IsType<TextContentBlock>(block).Text;
    }
}
