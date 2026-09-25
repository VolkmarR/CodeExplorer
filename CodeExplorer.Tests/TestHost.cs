using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CodeExplorer.Index;
using CodeExplorer.Operator;
using CodeExplorer.Reading;
using CodeExplorer.Refresh;
using DuckDB.NET.Data;
using LibGit2Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

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
    /// <param name="warmUpOnStart">
    ///     Switches on the background warm-up, which is off everywhere else so that a restart in a test
    ///     is a cold wake and nothing restores behind the assertions.
    /// </param>
    /// <param name="authenticated">
    ///     Points the host at <see cref="Tenant" />. Off everywhere else, because an unauthenticated
    ///     server is the shape ADR-0004 requires an empty appsettings to produce and therefore the one
    ///     the rest of the suite should be proving still works.
    /// </param>
    /// <param name="extensionDirectory">
    ///     Where DuckDB looks for the fts extension. Absent everywhere else, which leaves DuckDB's own
    ///     default; the container arrangement (#14) is the one thing that sets it, so the one test that
    ///     asserts on it is the one that passes it.
    /// </param>
    /// <param name="maxCommitPaths">
    ///     Lowered far below any real ceiling so that a fixture commit of a dozen paths counts as a mass
    ///     commit. A fixture large enough to cross the shipped default would take longer to build than
    ///     the rest of the suite takes to run.
    /// </param>
    /// <param name="transferStallSeconds">
    ///     Lowered to seconds so a stalled remote gives up within a test's patience. The limit is
    ///     libgit2's and process-wide, so only a test that runs alone may set it (see
    ///     <c>StalledRemoteTests</c>).
    /// </param>
    public TestHost(SearchEngine engine, int? drainSeconds = null, long? minimumFreeBytes = null,
        bool warmUpOnStart = false, bool authenticated = false, string? extensionDirectory = null,
        int? maxCommitPaths = null, int? transferStallSeconds = null)
    {
        _engine = engine;
        _drainSeconds = drainSeconds;
        _minimumFreeBytes = minimumFreeBytes;
        _warmUpOnStart = warmUpOnStart;
        _authenticated = authenticated;
        _extensionDirectory = extensionDirectory;
        _maxCommitPaths = maxCommitPaths;
        _transferStallSeconds = transferStallSeconds;
        Factory = Build();
    }

    private readonly SearchEngine _engine;
    private readonly int? _drainSeconds;
    private readonly long? _minimumFreeBytes;
    private readonly bool _warmUpOnStart;
    private readonly bool _authenticated;
    private readonly string? _extensionDirectory;
    private readonly int? _maxCommitPaths;
    private readonly int? _transferStallSeconds;

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
            if (_extensionDirectory is { } extensions) builder.UseSetting("Index:ExtensionDirectory", extensions);
            if (_drainSeconds is { } seconds)
                builder.UseSetting("Index:DrainSeconds", seconds.ToString(CultureInfo.InvariantCulture));
            if (_minimumFreeBytes is { } bytes)
                builder.UseSetting("Refresh:MinimumFreeBytes", bytes.ToString(CultureInfo.InvariantCulture));
            if (_maxCommitPaths is { } paths)
                builder.UseSetting("History:MaxCommitPaths", paths.ToString(CultureInfo.InvariantCulture));
            if (_transferStallSeconds is { } stall)
                builder.UseSetting("Git:TransferStallSeconds", stall.ToString(CultureInfo.InvariantCulture));
            if (_warmUpOnStart) builder.UseSetting("Refresh:WarmUpOnStart", "true");
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
        Factory.Dispose();
        Factory = Build();
    }

    /// <summary>
    ///     A restart whose wipe took the control database too, so the new server has only its backup
    ///     to start from. The file goes between the stop and the start, which is the only moment it can:
    ///     a running server holds it open for the life of the process (#172).
    /// </summary>
    public void RestartWithoutControlDatabase()
    {
        Factory.Dispose();
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
    public async Task InterruptBuildAsync(string slug)
    {
        using var lease = await OpenIndexAsync(slug);
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "DELETE FROM index_info";
        await command.ExecuteNonQueryAsync(Ct);
    }

    private string ControlDatabaseFile => Path.Combine(DataDirectory, "control.duckdb");

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
    private string DurableDirectory => Path.Combine(_root, "durable");

    /// <summary>A project's index file, for a test asserting that a deletion took it or a build made it.</summary>
    public string IndexFile(string slug) => Path.Combine(DataDirectory, "indexes", slug + ".duckdb");

    /// <summary>The folder holding every local copy of one project.</summary>
    public string ProjectClones(string slug) => Path.Combine(DataDirectory, "clones", slug);

    /// <summary>
    ///     A path under the host's data directory for a file the test itself writes, or names and never
    ///     writes. Nothing in the server looks there, and the directory goes when the host does.
    /// </summary>
    public string ScratchFile(string name) => Path.Combine(DataDirectory, name);

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
    ///     A refused delete is tried again for a moment, because a file DuckDB has just removed can
    ///     linger: closing a database's last connection deletes its <c>.wal</c>, and while another
    ///     process — a virus scanner, the search indexer — still has the freshly written file open, it
    ///     is listed but refuses deletion with "access denied". Measured on this machine it is gone
    ///     61 ms later, and before this retry it failed whichever test's cleanup met it, so a
    ///     different test each run. The same file can also vanish between being listed and having
    ///     its attributes cleared, which needs nothing more than skipping it. It does not defeat a file
    ///     mapped into this process, which no wait releases: a caller with such a tree passes
    ///     <paramref name="retry" /> false and catches the failure itself.
    /// </summary>
    public static void DeleteTree(string path, bool retry = true)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                file.Attributes = FileAttributes.Normal;
            }
            catch (Exception gone) when (gone is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted since it was listed: nothing left to clear.
            }
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception refused) when (retry && attempt < DeleteAttempts
                                             && refused is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(DeleteRetryMilliseconds);
                // The attempt that threw may have removed everything but the directory's own entry.
                if (!Directory.Exists(path)) return;
            }
        }
    }

    /// <summary>
    ///     Half a second in all, several times the 61 ms a lingering WAL was measured to need, and short
    ///     enough that a file that will never go fails the cleanup promptly instead of stalling it.
    /// </summary>
    private const int DeleteAttempts = 10;

    private const int DeleteRetryMilliseconds = 50;

    /// <summary>Builds a non-bare repository with one commit holding the given files and returns its path.</summary>
    public string CreateGitRepository(string name, Dictionary<string, string> files) =>
        Commit(CreateEmptyGitRepository(name), files);

    /// <summary>
    ///     The same with each file's bytes written as given, for a test about how content is decoded:
    ///     the string overload always writes UTF-8, so it cannot commit a Windows-1252 or UTF-16 file.
    /// </summary>
    public string CreateGitRepository(string name, Dictionary<string, byte[]> files) =>
        Commit(CreateEmptyGitRepository(name), files);

    /// <summary>
    ///     An initialised repository with no commits: a remote that really is empty, as opposed to a
    ///     clone whose HEAD lost the branch it named. The two look alike from HEAD's tip and must not
    ///     be reported alike.
    /// </summary>
    public string CreateEmptyGitRepository(string name)
    {
        string path = Path.Combine(_root, "fixtures", name);
        Repository.Init(path);
        return path;
    }

    /// <summary>
    ///     Renames the branch a fixture's HEAD is on, which is what a default branch renamed upstream
    ///     looks like from here: the old name is gone, so the next fetch prunes it out of the clone
    ///     that was made while HEAD still named it.
    /// </summary>
    public void RenameDefaultBranch(string name, string branch)
    {
        using var repo = new Repository(FixturePath(name));
        repo.Branches.Rename(repo.Head, branch);
    }

    /// <summary>The commit a fixture's HEAD is at, for a test that resets the fixture back to it later.</summary>
    public string HeadOf(string name)
    {
        using var repo = new Repository(FixturePath(name));
        return repo.Head.Tip.Sha;
    }

    /// <summary>
    ///     <c>reset --hard</c> on a fixture, which the next forced fetch mirrors as a force push would.
    ///     Hard, so a commit made on top afterwards starts from that commit's tree.
    /// </summary>
    public void ResetGitRepository(string name, string sha)
    {
        using var repo = new Repository(FixturePath(name));
        repo.Reset(ResetMode.Hard, repo.Lookup<Commit>(sha));
    }

    /// <summary>
    ///     Points a repository's HEAD at a branch that does not exist: the state #31 left a clone in,
    ///     and, on a fixture, a remote whose own default branch cannot be resolved. Written as a file
    ///     because that is all HEAD is, and because libgit2 refuses such a symbolic reference.
    /// </summary>
    public static void BreakHead(string gitDirectory, string branch) =>
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), $"ref: refs/heads/{branch}\n");

    /// <summary>The bare clone of one repository, for a test that has to look at it or break it.</summary>
    public string ClonePath(string project, string repository) =>
        Path.Combine(DataDirectory, "clones", project, repository + ".git");

    /// <summary>The <c>.git</c> directory of a fixture, which is non-bare.</summary>
    public string FixtureGitPath(string name) => Path.Combine(FixturePath(name), ".git");

    /// <summary>
    ///     Adds a commit to a fixture already created, which is what a push to the remote looks like
    ///     from here: the clone made earlier still holds the old tree until something fetches.
    /// </summary>
    public string CommitToGitRepository(string name, Dictionary<string, string> files) =>
        Commit(Path.Combine(_root, "fixtures", name), files);

    /// <summary>
    ///     A commit with an author and a message of its own, for a history test: the default fixture
    ///     author and subject are the same on every commit, which is exactly what a test asserting who
    ///     wrote what cannot use. The date advances with <paramref name="minute" /> so that two commits
    ///     are orderable, which at the shared epoch they are not.
    /// </summary>
    public string CommitToGitRepositoryAs(string name, Dictionary<string, string> files, string subject,
        string authorName, string authorEmail, int minute) =>
        Commit(Path.Combine(_root, "fixtures", name), files, subject,
            new Signature(authorName, authorEmail, DateTimeOffset.UnixEpoch.AddMinutes(minute)));

    /// <summary>
    ///     A commit that deletes paths rather than writing them, for a history test about a file that
    ///     the recorded window changed and HEAD no longer holds. The two cases cannot be one call: a
    ///     deletion is an absent key, and an absent key is indistinguishable from a file the commit
    ///     simply did not touch.
    /// </summary>
    public void RemoveInGitRepositoryAs(string name, IEnumerable<string> paths, string subject, string authorName,
        string authorEmail, int minute)
    {
        using var repo = new Repository(FixturePath(name));
        foreach (string relative in paths) Commands.Remove(repo, relative);
        var author = new Signature(authorName, authorEmail, DateTimeOffset.UnixEpoch.AddMinutes(minute));
        repo.Commit(subject, author, author);
    }

    /// <summary>
    ///     A commit that moves paths, content unchanged — one commit holding the removal of each old
    ///     path and the addition of each new one, which is how git records a move and the only shape
    ///     libgit2's rename detection reports as <c>renamed</c>. A remove commit followed by an add
    ///     commit is two unrelated changes, and a test built that way proves nothing about renames
    ///     (#131).
    /// </summary>
    /// <param name="name">The fixture repository.</param>
    /// <param name="moves">Old repository-relative path to new, all in one commit.</param>
    /// <param name="subject">The commit's subject.</param>
    /// <param name="authorName">Who to record as the author.</param>
    /// <param name="authorEmail">Their address.</param>
    /// <param name="minute">Minutes past the epoch, which is how these fixtures order their history.</param>
    public void MoveInGitRepositoryAs(string name, Dictionary<string, string> moves, string subject,
        string authorName, string authorEmail, int minute)
    {
        string root = FixturePath(name);
        using var repo = new Repository(root);
        foreach ((string from, string to) in moves)
        {
            string source = Path.Combine(root, from);
            string target = Path.Combine(root, to);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            Commands.Remove(repo, from);
            Commands.Stage(repo, to);
        }

        var author = new Signature(authorName, authorEmail, DateTimeOffset.UnixEpoch.AddMinutes(minute));
        repo.Commit(subject, author, author);
    }

    // Encoding.UTF8.GetBytes writes no byte order mark, as File.WriteAllText did before the bytes overload.
    private static string Commit(string path, Dictionary<string, string> files, string subject = "fixture",
        Signature? author = null) =>
        Commit(path, files.ToDictionary(file => file.Key, file => Encoding.UTF8.GetBytes(file.Value)), subject,
            author);

    private static string Commit(string path, Dictionary<string, byte[]> files, string subject = "fixture",
        Signature? author = null)
    {
        using var repo = new Repository(path);
        foreach ((string relative, byte[] content) in files)
        {
            string full = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
            Commands.Stage(repo, relative);
        }

        author ??= new Signature("Test", "test@example.invalid", DateTimeOffset.UnixEpoch);
        repo.Commit(subject, author, author);
        return path;
    }

    /// <summary>Where <see cref="CreateGitRepository(string, Dictionary{string, string})" /> put the fixture with this name.</summary>
    public string FixturePath(string name) => Path.Combine(_root, "fixtures", name);

    /// <summary>Deletes a fixture, which is what a remote that was removed or renamed looks like from here.</summary>
    public void RemoveGitRepository(string name) => DeleteTree(FixturePath(name));

    /// <summary>
    ///     What the host was pointed at. The layout under it is named by the members above; it is public
    ///     for a test asserting that a message a caller reads does not disclose it.
    /// </summary>
    public string DataDirectory => Path.Combine(_root, "data");

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
    ///     The first column of every row the query returns, read through a lease of its own. Tests
    ///     assert on a list of strings — paths, reasons, schema names — often enough that spelling out
    ///     the reader each time is the whole body of the test.
    /// </summary>
    public async Task<List<string>> ScalarsAsync(string slug, string sql)
    {
        using var lease = await OpenIndexAsync(slug);
        return await ScalarsAsync(lease, sql);
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
        using var http = Fixture();
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
    public async Task<ProjectOverviewDetail> OverviewDetailAsync(string project, string query = "")
    {
        using var http = CreateClient();
        var detail = await http.GetFromJsonAsync<ProjectOverviewDetail>($"/api/projects/{project}/overview{query}", Ct);
        Assert.NotNull(detail);
        return detail;
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
