using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CodeExplorer.Control;
using CodeExplorer.Git;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using CodeExplorer.Operator;
using CodeExplorer.Reading;
using CodeExplorer.Refresh;
using DuckDB.NET.Data;
using LibGit2Sharp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Tests;

/// <summary>
///     One in-process server with its own data directory and a pinned search engine, plus the fixture
///     steps every index-backed test repeats: a git repository with one commit (from
///     <see cref="GitFixtures" />, whose root the server's directories share), a project, its
///     repositories, a build, and an MCP client bound to the project's route.
/// </summary>
public sealed class TestHost : GitFixtures
{
    /// <param name="engine">Pinned in every test: the two engines build different schemas and rank differently.</param>
    /// <param name="settings">
    ///     Configuration the host starts with, keyed as <c>appsettings.json</c> spells it, each value
    ///     written out invariantly and a null one left unset. Every setting a test passes departs from
    ///     the shipped default for a reason, and the class that passes it says what that reason is.
    /// </param>
    public TestHost(SearchEngine engine, params (string Key, object? Value)[] settings)
        : this(engine, false, null, settings)
    {
    }

    /// <param name="engine">Pinned in every test: the two engines build different schemas and rank differently.</param>
    /// <param name="authenticated">
    ///     Points the host at <see cref="Tenant" />. Off everywhere else, because an unauthenticated
    ///     server is the shape ADR-0004 requires an empty appsettings to produce and therefore the one
    ///     the rest of the suite should be proving still works.
    /// </param>
    /// <param name="webUiPage">
    ///     An <c>index.html</c> for the host to serve as the web UI, from a web root of its own. Absent
    ///     everywhere else: <c>wwwroot</c> is a Vite build that exists on a developer's machine and not
    ///     on CI, so a test that reaches the SPA fallback has to bring the page it falls back to.
    /// </param>
    public TestHost(SearchEngine engine, bool authenticated = false, string? webUiPage = null)
        : this(engine, authenticated, webUiPage, [])
    {
    }

    private TestHost(SearchEngine engine, bool authenticated, string? webUiPage,
        (string Key, object? Value)[] settings)
    {
        if (webUiPage is not null)
        {
            Directory.CreateDirectory(WebRoot);
            File.WriteAllText(Path.Combine(WebRoot, "index.html"), webUiPage);
            _servesWebUi = true;
        }

        _engine = engine;
        _authenticated = authenticated;
        // On everywhere but where the refusal is the subject, because every fixture is a repository on
        // this disk; the shipped default is off (GHSA-5373-pppr-q3q9).
        _settings[RepositoryUrl.AllowLocalSetting] = "true";
        foreach ((string key, object? value) in settings)
            _settings[key] = value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        Factory = Build();
    }

    private readonly SearchEngine _engine;
    private readonly bool _authenticated;
    private readonly bool _servesWebUi;

    /// <summary>
    ///     What <see cref="Build" /> configures beyond the directories and the engine, by key. Compared
    ///     ignoring case as configuration compares them, so a key spelled differently replaces the
    ///     default rather than being applied beside it.
    /// </summary>
    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Private, so a test cannot build a client that bypasses <see cref="CreateClient" /> or reach a
    ///     path the host has not named. The interface is the test surface (#41).
    /// </summary>
    private WebApplicationFactory<Program> Factory { get; set; }

    /// <summary>
    ///     The server's services, for the few tests whose subject is one of them — a cookie minted the
    ///     way the server mints it, a protector with the server's purpose, a search run without HTTP.
    /// </summary>
    public IServiceProvider Services => Factory.Services;

    /// <summary>
    ///     What the server logged, across restarts, for a test whose subject is a line only the operator
    ///     reads: a detail kept out of a caller-facing message still has to reach someone.
    /// </summary>
    public LogProbe Logs { get; } = new();

    private WebApplicationFactory<Program> Build() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Warning and up only: that is every line a test asserts on so far, and capturing each
            // Information line of every host in the suite would hold the whole refresh chatter in memory.
            builder.ConfigureServices(services => services.AddLogging(logging =>
                logging.AddProvider(Logs).AddFilter<LogProbe>(null, LogLevel.Warning)));
            builder.UseSetting("Storage:DataDirectory", DataDirectory);
            builder.UseSetting("Storage:DurableDirectory", DurableDirectory);
            builder.UseSetting("Index:SearchEngine", _engine.ToString());
            foreach ((string key, string? value) in _settings)
                if (value is not null)
                    builder.UseSetting(key, value);
            // Through the static-file options and not UseWebRoot, which the minimal host reads before this
            // callback runs and so ignores: the page served was the developer's own wwwroot build.
            if (_servesWebUi)
                builder.ConfigureServices(services => services.PostConfigure<StaticFileOptions>(options =>
                    options.FileProvider = new PhysicalFileProvider(WebRoot)));
            if (!_authenticated) return;

            foreach ((string key, string value) in Tenant.Configuration) builder.UseSetting(key, value);
            builder.ConfigureTestServices(Tenant.StubDiscovery);
        });

    /// <summary>The host's warm-up service, for a test that awaits the pass it does rather than polling.</summary>
    public WarmUpService WarmUpService => Factory.Services.GetRequiredService<WarmUpService>();

    /// <summary>
    ///     Stops the server and starts a new one over the same directories, which is what a scale to
    ///     zero and a wake look like from here: the singletons, the attach state and the in-memory
    ///     refresh statuses are all gone, and only what is on disk and in the durable store remains.
    /// </summary>
    public void Restart()
    {
        Stop();
        Factory = Build();
    }

    /// <summary>
    ///     Disposes the server and waits until its DuckDB instances have let go of their files (#285).
    ///     <c>Factory.Dispose()</c> alone can return before they do: the app's own <c>RunAsync</c> disposes
    ///     the host on the entry-point thread once the host stops, and the factory's dispose, finding the
    ///     service provider already being disposed, returns without waiting for it. Measured in full-suite
    ///     runs, the control database stayed open for 20 to 80 ms after it returned. A new server started
    ///     in that window opens the same path, and DuckDB.NET, which keeps one native instance per path
    ///     while any connection to it is open, hands it the old instance: the old server's projects, attach
    ///     state and all, whatever the file on disk now holds. Waiting on the files rather than on the
    ///     host, because nothing the factory exposes completes when the entry point has finished. A closed
    ///     file is enough: DuckDB.NET closes the instance and drops it from its cache under one lock, and
    ///     an open that arrives in between waits on that lock and then opens a fresh instance.
    /// </summary>
    private void Stop()
    {
        Factory.Dispose();
        AwaitClosed(ControlDatabaseFile);
        AwaitClosed(IndexInstanceFile);
    }

    /// <summary>
    ///     Far past the 80 ms measured, so only a server that never lets go reaches it, and that one fails
    ///     in <see cref="AwaitClosed" />, named, instead of as a wrong answer from the next server.
    /// </summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     As short as a sleep usually gets without spinning a core: Windows rounds it up to its timer
    ///     tick, about 15 ms, so a wait overshoots the release by at most that.
    /// </summary>
    private const int ClosePollMilliseconds = 5;

    /// <summary>
    ///     Returns once nothing holds <paramref name="path" /> open, or at once when it does not exist:
    ///     a server that never resolved the control database or the index instance never created them.
    /// </summary>
    private static void AwaitClosed(string path)
    {
        var deadline = DateTime.UtcNow + CloseTimeout;
        while (true)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Safe to swallow: no file is a file nothing holds.
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Past the deadline the sharing violation propagates, and its message names the file.
                // Safe to swallow: the old server is still disposing, and the loop is the wait for it.
                Thread.Sleep(ClosePollMilliseconds);
            }
        }
    }

    /// <summary>
    ///     A restart with local repositories switched off: an operator closing the setting on a server
    ///     that already stores some, whose repositories must stop being read.
    /// </summary>
    public void RestartWithoutLocalRepositories()
    {
        _settings[RepositoryUrl.AllowLocalSetting] = "false";
        Restart();
    }

    /// <summary>
    ///     A restart whose wipe took the control database too, so the new server has only its backup
    ///     to start from. The file goes between the stop and the start, which is the only moment it can:
    ///     a running server holds it open for the life of the process (#172).
    /// </summary>
    public void RestartWithoutControlDatabase()
    {
        Stop();
        DeleteDatabase(ControlDatabaseFile);
        Factory = Build();
    }

    /// <summary>
    ///     Removes a project's index file, which is what the container's disk being wiped leaves behind:
    ///     a project that exists, has a durable copy, and has nothing local to answer from.
    /// </summary>
    public void DeleteIndexFile(string slug) => DeleteDatabase(IndexFile(slug));

    /// <summary>
    ///     The file a process killed mid-build leaves behind: every table present and nothing saying the
    ///     build finished. A build writes <c>index_info</c> last, so removing its row is exactly that
    ///     state, and the project must read as not built rather than as built from half its files.
    /// </summary>
    public Task InterruptBuildAsync(string slug) => ExecuteAsync(slug, "DELETE FROM index_info");

    private string ControlDatabaseFile => Path.Combine(DataDirectory, "control.duckdb");

    /// <summary>The index instance's default catalog, a file in the index folder like any project's.</summary>
    private string IndexInstanceFile => IndexFile("instance");

    /// <summary>
    ///     A connection to the running server's control database, for a test that looks behind
    ///     <see cref="Control.ControlDatabase" />. DuckDB.NET keeps one native instance per file in a process,
    ///     so this is the server's instance and not a second one: it sees what the server wrote, and
    ///     the server sees what it changes.
    /// </summary>
    public async Task<DuckDBConnection> OpenControlDatabaseAsync()
    {
        var connection = new DuckDBConnection($"Data Source={ControlDatabaseFile}");
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>
    ///     A connection to the server's index instance, the one every project and shadow is attached to,
    ///     for a test that has to hold a catalog open the way a straggling query does. The same process
    ///     cache as <see cref="OpenControlDatabaseAsync" />: this is the server's instance, not a second one.
    /// </summary>
    public async Task<DuckDBConnection> OpenIndexInstanceAsync()
    {
        var connection = new DuckDBConnection($"Data Source={IndexInstanceFile}");
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>
    ///     Makes every checkpoint on the index instance fail until disposed, through DuckDB's own fault
    ///     switch: how a test gets a finished file that cannot be written out. The switch is
    ///     instance-wide, so disposing it is what lets anything else in the test checkpoint again.
    /// </summary>
    public async Task<IAsyncDisposable> FailingCheckpointsAsync()
    {
        var instance = await OpenIndexInstanceAsync();
        await instance.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'before_truncate'", Ct);
        return new CheckpointsRestored(instance);
    }

    private sealed class CheckpointsRestored(DuckDBConnection instance) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using (instance) await instance.ExecuteAsync("SET GLOBAL debug_checkpoint_abort = 'none'", Ct);
        }
    }

    /// <summary>A statement run on the server's control database, through a connection of its own.</summary>
    public async Task ExecuteOnControlDatabaseAsync(string sql)
    {
        using var connection = await OpenControlDatabaseAsync();
        await connection.ExecuteAsync(sql, Ct);
    }

    /// <summary>A database file and the write-ahead log beside it, which a stop leaves behind.</summary>
    private static void DeleteDatabase(string path)
    {
        File.Delete(path);
        File.Delete(path + ".wal");
    }

    /// <summary>Where the folder durable store keeps a project's Parquet set, whether or not it has one.</summary>
    public string DurableIndexDirectory(string slug) => Path.Combine(DurableDirectory, "indexes", slug);

    /// <summary>The control database's backup, which is a file under its own prefix rather than Parquet.</summary>
    public string DurableControlBackup => Path.Combine(DurableDirectory, "control", "control.duckdb");

    /// <summary>The folder standing in for a blob container, which is what an unconfigured app uses.</summary>
    private string DurableDirectory => Path.Combine(Root, "durable");

    /// <summary>The web root of a host given a <c>webUiPage</c>, in place of the project's own <c>wwwroot</c>.</summary>
    private string WebRoot => Path.Combine(Root, "wwwroot");

    /// <summary>A project's index file, for a test asserting that a deletion took it or a build made it.</summary>
    public string IndexFile(string slug) => Path.Combine(DataDirectory, "indexes", slug + ".duckdb");

    /// <summary>The folder holding every local copy of one project.</summary>
    public string ProjectClones(string slug) => Path.Combine(DataDirectory, "clones", slug);

    /// <summary>
    ///     A path under the host's data directory for a file the test itself writes, or names and never
    ///     writes. Nothing in the server looks there, and the directory goes when the host does.
    /// </summary>
    public string ScratchFile(string name) => Path.Combine(DataDirectory, name);

    /// <summary>
    ///     Records the query plan of every read this host makes into <paramref name="directory" /> until
    ///     disposed, and of no other host's (see <see cref="QueryPlan.Recording" />).
    /// </summary>
    public IDisposable RecordPlans(string directory) => QueryPlan.Recording(directory, DataDirectory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Stops the server before the root goes, because it holds its databases open under it.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) Factory.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The bare clone of one repository, for a test that has to look at it or break it.</summary>
    public string ClonePath(string project, string repository) =>
        Path.Combine(DataDirectory, "clones", project, repository + ".git");

    /// <summary>
    ///     Makes the local copy of a repository shallow at its HEAD commit, as every copy was before
    ///     ADR-0007 (depth 1), cloning the fixture there first when there is no copy yet. libgit2's local
    ///     transport refuses a shallow fetch ("shallow fetch is not supported by the local transport"),
    ///     so the copy is cloned full and made shallow the way git records it: a <c>shallow</c> file
    ///     naming the boundary commit. libgit2 reads it as a graft, so the boundary has no parent and the
    ///     copy reports itself shallow, exactly as a real one does; the older objects the pack still
    ///     holds are out of reach of any walk.
    /// </summary>
    /// <param name="project">The project the repository belongs to.</param>
    /// <param name="repository">The repository whose local copy this is.</param>
    /// <param name="fixture">The fixture it was added from, whose path is the stored URL.</param>
    /// <returns>The copy's path.</returns>
    public string MakeLocalCopyShallow(string project, string repository, string fixture)
    {
        string path = ClonePath(project, repository);
        if (!Directory.Exists(path)) Repository.Clone(FixturePath(fixture), path, new CloneOptions { IsBare = true });
        using var clone = new Repository(path);
        File.WriteAllText(Path.Combine(path, "shallow"), clone.Head.Tip.Sha + "\n");
        return path;
    }

    /// <summary>
    ///     What the host was pointed at. The layout under it is named by the members above; it is public
    ///     for a test asserting that a message a caller reads does not disclose it.
    /// </summary>
    public string DataDirectory => Path.Combine(Root, "data");

    /// <param name="slug">The project's slug, which is also its display name unless <paramref name="name" /> says otherwise.</param>
    /// <param name="singleRepository">Declares the project single-repository (ADR-0006).</param>
    /// <param name="name">A display name, for a test asserting that the name and not the slug is shown.</param>
    public async Task CreateProjectAsync(string slug, bool singleRepository = false, string? name = null)
    {
        using var http = Fixture();
        using var response =
            await http.PostAsJsonAsync("/api/projects", new { slug, name = name ?? slug, singleRepository }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public async Task AddRepositoryAsync(string project, string slug, string url, string? credential = null)
    {
        using var http = Fixture();
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

    /// <summary>Asks for a refresh, waits for it, and answers the error it failed with.</summary>
    public async Task<string> FailedRefreshErrorAsync(string project)
    {
        using (var response = await RequestRefreshAsync(project))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForRefreshesAsync();

        var status = await RefreshStatusAsync(project);
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.NotNull(status.Error);
        return status.Error;
    }

    /// <summary>Asks for a refresh and returns the response, for a test that asserts on the refusal.</summary>
    public async Task<HttpResponseMessage> RequestRefreshAsync(string project)
    {
        using var http = Fixture();
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
    ///     Opens a lease and hands it back completed, so the project's pool holds exactly this
    ///     connection for the next lease to borrow: the starting state of every test of the pool.
    /// </summary>
    public async Task<DuckDBConnection> PooledConnectionAsync(string slug)
    {
        using var lease = await OpenIndexAsync(slug);
        lease.Completed();
        return lease.Connection;
    }

    /// <summary>
    ///     The first column of every row the query returns, read through a lease of its own. Tests
    ///     assert on a list of strings — paths, reasons, schema names — often enough that spelling out
    ///     the reader each time is the whole body of the test.
    /// </summary>
    public async Task<List<string>> ScalarsAsync(string slug, string sql)
    {
        using var lease = await OpenIndexAsync(slug);
        var values = await ScalarsAsync(lease, sql);
        // Completed as a reader would be, so tests of the pool read through pooled connections.
        lease.Completed();
        return values;
    }

    /// <summary>
    ///     Runs a statement against a project's index, for a test that has to put the index into a state
    ///     a build would not produce — an index with no history, as every index built before ADR-0007 is.
    /// </summary>
    public async Task ExecuteAsync(string slug, string sql)
    {
        using var lease = await OpenIndexAsync(slug);
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
        lease.Completed();
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

    public Task<RefreshStatus> RefreshStatusAsync(string project) =>
        GetJsonAsync<RefreshStatus>($"/api/projects/{project}/refresh");

    /// <summary>A project of the given repositories (slug to files), created, added and indexed.</summary>
    public async Task<IndexSummary> IndexedProjectAsync(string project,
        Dictionary<string, Dictionary<string, string>> repositories, bool singleRepository = false)
    {
        await CreateProjectAsync(project, singleRepository);
        foreach ((string slug, var files) in repositories)
            await AddRepositoryAsync(project, slug, CreateGitRepository(slug, files));
        return await RefreshAsync(project);
    }

    /// <summary>
    ///     A project indexed end to end with no repositories, so the index is real and only the history
    ///     is missing — a state a refresh cannot reach, because a project without repositories is not
    ///     built.
    /// </summary>
    public async Task HistorylessProjectAsync(string project)
    {
        await CreateProjectAsync(project);
        var shadow = await Indexes.CreateShadowAsync(project, Ct);
        await Services.GetRequiredService<OverviewBuilder>().FillAsync(shadow, false, Ct);
        await shadow.CompleteAsync(false, _ => { }, Ct);
        await PublishAsync(shadow);
    }

    /// <summary>
    ///     Builds and swaps in a project's index from its local copies as they lie on disk, through the
    ///     build a refresh runs but without the fetch before it — the index a server built from copies
    ///     it no longer would read as they are, such as a shallow one from before ADR-0007.
    /// </summary>
    public async Task BuildFromLocalCopiesAsync(string project)
    {
        var repositories = await Services.GetRequiredService<ControlDatabase>().ListRepositoriesAsync(project, Ct);
        var opened = repositories
            .Select(repository => new OpenedRepository(repository,
                new LocalCopy(new Repository(ClonePath(project, repository.Slug)))))
            .ToList();
        try
        {
            using var shadow = await Indexes.CreateShadowAsync(project, Ct);
            await Services.GetRequiredService<IndexBuilder>().FillAsync(shadow, opened, repositories, false, _ => { },
                Ct);
            await PublishAsync(shadow);
        }
        finally
        {
            // Closed, or Windows keeps the pack files and the refresh after this cannot clone over them.
            foreach (var open in opened) open.LocalCopy.Dispose();
        }
    }

    /// <summary>
    ///     Stores and swaps in a shadow a test filled by hand, the way a refresh publishes one, for a
    ///     project nothing is deleting.
    /// </summary>
    public async Task PublishAsync(ShadowIndex shadow) =>
        Assert.True(await Indexes.PublishShadowAsync(shadow,
            new PublishCondition(Indexes.DiscardCount(shadow.Slug), _ => Task.FromResult(true)), _ => { }, Ct));

    /// <summary>Replaces a project's excluded-paths setting (#216), asserting the server took it.</summary>
    public async Task SetExcludedPathsAsync(string project, string[] patterns)
    {
        using var http = CreateClient();
        using var response = await http.PutAsJsonAsync($"/api/projects/{project}/excluded-paths", new { patterns }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The overview page's read, as the browser receives it, with the page's filters in <paramref name="query" />.</summary>
    public Task<ProjectOverviewDetail> OverviewDetailAsync(string project, string query = "") =>
        GetJsonAsync<ProjectOverviewDetail>($"/api/projects/{project}/overview{query}");

    /// <summary>
    ///     A GET whose answer is JSON, read as <typeparamref name="T" /> and asserted present, through the
    ///     client the fixture steps use. A non-success status throws, so a test that reads an answer
    ///     cannot be passed by a refusal; a test whose subject is the status sends its own request.
    ///     Read with the server's own HTTP options, so an enum comes back from the spelling the server
    ///     wrote; what that spelling is, is <see cref="HttpJsonTests" />' question.
    /// </summary>
    public async Task<T> GetJsonAsync<T>(string path)
    {
        using var http = Fixture();
        var options = Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var value = await http.GetFromJsonAsync<T>(path, options, Ct);
        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    ///     A GET whose answer is JSON, read as the bytes spell it rather than as a record. A typed read
    ///     turns either spelling of an enum value back into the same member, so a test about what the
    ///     browser compares against has to read the text.
    /// </summary>
    public async Task<JsonNode> GetJsonNodeAsync(string path)
    {
        using var http = Fixture();
        var value = JsonNode.Parse(await http.GetStringAsync(path, Ct));
        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    ///     The client the fixture steps use, which is authenticated exactly when the host is. A project
    ///     has to exist before anything about protecting it can be asserted, and on a host with a tenant
    ///     even creating one needs a token — so the fixture presents one rather than every
    ///     authentication test starting with a sign-in it is not testing.
    /// </summary>
    private HttpClient Fixture() => CreateClient(_authenticated ? Tenant.Token() : null);

    /// <summary>
    ///     An HTTP client that presents a bearer token, or none when there is nothing to present. The
    ///     one place a test says how it is authenticated, so that a test asserting on a tool's answer
    ///     does not also spell out a header. Redirects are not followed: where a caller is sent is what
    ///     several of these tests are about.
    /// </summary>
    public HttpClient CreateClient(string? token = null)
    {
        var http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>
    ///     A GET that carries no Host header at all, as an HTTP/1.0 client sends it. Through the test
    ///     server rather than <see cref="CreateClient" />, because an <see cref="HttpClient" /> always
    ///     fills a Host in from its base address.
    /// </summary>
    public async Task<HttpStatusCode> GetWithoutHostAsync(string path)
    {
        var context = await Factory.Server.SendAsync(request =>
        {
            request.Request.Method = HttpMethods.Get;
            request.Request.Path = path;
            request.Request.Headers.Host = default;
        }, Ct);
        return (HttpStatusCode)context.Response.StatusCode;
    }

    public async Task<McpClient> ConnectAsync(string slug, string? token = null)
    {
        var http = CreateClient(token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, $"/projects/{slug}/mcp") },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>
    ///     Calls a tool and returns its single text block. A refused call — the SDK reports a handler's
    ///     <see cref="McpException" /> as an error result rather than throwing on the client — is
    ///     re-raised here with the tool's text, so an assertion on an answer cannot be met by a refusal
    ///     that happens to contain the right words. A test that wants the refusal asserts on the throw.
    /// </summary>
    public static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        var block = Assert.Single(result.Content);
        string text = Assert.IsType<TextContentBlock>(block).Text;
        if (result.IsError == true) throw new McpException(text);
        return text;
    }
}
