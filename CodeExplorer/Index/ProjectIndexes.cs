using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     Which engine answers text searches. Configured as <c>Index:SearchEngine</c>; absent means
///     <see cref="Auto" />, the local-development default (ADR-0004).
/// </summary>
public enum SearchEngine
{
    /// <summary>Full-text if <c>INSTALL fts</c> succeeds, substring scan otherwise. Never for tests.</summary>
    Auto,

    /// <summary>Full-text, or fail at startup. Pin this in a test that asserts BM25 behaviour.</summary>
    Fts,

    /// <summary>Substring scan only, even when the extension is installed. Pin this to test the offline path.</summary>
    Substring
}

/// <summary>
///     A connection bound to one project for one unit of work, and the record that the work is in
///     flight so a swap waits for it. Dispose it as soon as that unit of work is done and never keep
///     it: a swap detaches the catalog underneath, and the next statement on a connection that held
///     <c>USE</c> across it fails with <c>Catalog does not exist</c> (ADR-0003).
/// </summary>
public sealed class IndexLease(DuckDBConnection connection, Action release) : IDisposable
{
    private int _released;

    public DuckDBConnection Connection { get; } = connection;

    public void Dispose()
    {
        Connection.Dispose();
        // Guarded, because a double dispose would let a swap through while another lease still holds
        // the project — the one thing the drain exists to prevent.
        if (Interlocked.Exchange(ref _released, 1) == 0) release();
    }
}

/// <summary>
///     The shadow index a refresh fills (CONTEXT.md): a connection already <c>USE</c>ing the shadow
///     file, and the catalog name an appender targets. It is a second file next to the live one, so
///     the live index keeps answering queries until <see cref="ProjectIndexes.SwapShadowAsync" />.
/// </summary>
public sealed class ShadowIndex(DuckDBConnection connection, string catalog) : IDisposable
{
    public DuckDBConnection Connection { get; } = connection;

    /// <summary>What <c>CreateAppender</c> is given; it is not the project slug, so it is passed rather than derived.</summary>
    public string Catalog { get; } = catalog;

    public void Dispose() => Connection.Dispose();
}

/// <summary>
///     The one DuckDB instance every project index is attached to (ADR-0003). Owns the attach state,
///     hands out connections already bound with <c>USE</c>, and creates the shadow file a refresh
///     fills and then swaps in. Nothing else opens a project file.
/// </summary>
public sealed class ProjectIndexes : IDisposable
{
    /// <summary>
    ///     Bumped when the tables below change shape, so a Parquet export from an older build is rebuilt
    ///     from git instead of restored into a schema it no longer fits (#9).
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>
    ///     Default for <c>Index:DrainSeconds</c>: how long a swap waits for in-flight queries before
    ///     detaching anyway. Long enough that an ordinary grep or file read finishes first, short enough
    ///     that one abandoned query cannot hold a refresh open indefinitely. Past it the swap proceeds
    ///     and the straggler fails on its next statement, which ADR-0003 established is all
    ///     <c>DETACH</c> offers: it never blocks, so the wait is entirely ours and so is its limit.
    /// </summary>
    private const int DefaultDrainSeconds = 30;

    /// <summary>
    ///     Paths inside <c>files</c> stay repository-relative and <c>repo_id</c> scopes them; the
    ///     materialised <c>qualified_path</c> and <c>directory</c> keep the read paths join-free
    ///     (ADR-0003). A file that is committed but not indexed (binary, oversized) is still a row with
    ///     a <c>skip_reason</c>, so a tree listing shows it and a search can say why it was excluded.
    ///     <c>lines</c> carries one row per text line; a file's content is reassembled from them rather
    ///     than stored twice, which halves the file next to the proof of concept's layout.
    /// </summary>
    private const string Schema = """
                                  CREATE TABLE index_info (
                                      schema_version    INTEGER NOT NULL,
                                      built_at          TIMESTAMPTZ NOT NULL,
                                      fts_indexed       BOOLEAN NOT NULL,
                                      -- How this index named its files (ADR-0006). It is recorded here rather
                                      -- than read from the control database, because a qualified path cannot be
                                      -- parsed without it and Search answers from the index alone (ADR-0005).
                                      single_repository BOOLEAN NOT NULL);
                                  CREATE TABLE repositories (
                                      repo_id     INTEGER PRIMARY KEY,
                                      slug        VARCHAR NOT NULL UNIQUE,
                                      url         VARCHAR NOT NULL,
                                      head_commit VARCHAR NOT NULL,
                                      file_count  INTEGER NOT NULL,
                                      line_count  BIGINT NOT NULL);
                                  CREATE TABLE files (
                                      file_id        BIGINT PRIMARY KEY,
                                      repo_id        INTEGER NOT NULL,
                                      path           VARCHAR NOT NULL,
                                      qualified_path VARCHAR NOT NULL,
                                      directory      VARCHAR NOT NULL,
                                      name           VARCHAR NOT NULL,
                                      extension      VARCHAR NOT NULL,
                                      size_bytes     BIGINT NOT NULL,
                                      line_count     INTEGER NOT NULL,
                                      skip_reason    VARCHAR);
                                  CREATE TABLE lines (
                                      line_id     BIGINT PRIMARY KEY,
                                      file_id     BIGINT NOT NULL,
                                      line_number INTEGER NOT NULL,
                                      content     VARCHAR NOT NULL);
                                  """;

    // Attached databases and loaded extensions belong to the instance, and DuckDB.NET disposes the
    // instance once its last connection closes. This connection is never used for queries; it only
    // keeps the instance, and with it every ATTACH and the LOAD below, alive for the process.
    private readonly DuckDBConnection _anchor;

    // ATTACH is instance-wide, so two connections attaching the same slug at once race on the same
    // file. The gate serialises attach and detach; the set remembers what the instance already holds.
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private readonly HashSet<string> _attached = [];
    private readonly string _connectionString;

    private readonly string _directory;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger<ProjectIndexes> _logger;

    // One gate per project, kept for the life of the process: the count is bounded by the control
    // database, and a gate holds nothing but a reader count.
    private readonly Dictionary<string, SwapGate> _swapGates = [];
    private readonly Lock _swapGatesSync = new();

    public ProjectIndexes(IConfiguration configuration, ILogger<ProjectIndexes> logger)
    {
        _logger = logger;
        _directory = Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "indexes");
        Directory.CreateDirectory(_directory);
        _drainTimeout = TimeSpan.FromSeconds(configuration.GetValue("Index:DrainSeconds", DefaultDrainSeconds));
        // The instance needs a default catalog; this file holds nothing and exists only so that every
        // connection with this string shares one buffer pool and one memory_limit (ADR-0003).
        _connectionString = $"Data Source={Path.Combine(_directory, "instance.duckdb")}";

        // Synchronous on purpose: runs once at startup, before any request could cancel it.
        _anchor = new DuckDBConnection(_connectionString);
        _anchor.Open();

        var engine = configuration.GetValue("Index:SearchEngine", SearchEngine.Auto);
        FtsAvailable = engine != SearchEngine.Substring && TryLoadFts(engine, logger);
    }

    /// <summary>
    ///     True when the <c>fts</c> extension is loaded and builds create a BM25 index. False means
    ///     every search is a substring scan, which ranks differently; tests pin the engine for that reason.
    /// </summary>
    public bool FtsAvailable { get; }

    public void Dispose()
    {
        _anchor.Dispose();
        _attachGate.Dispose();
    }

    public string FilePath(string slug) => Path.Combine(_directory, slug + ".duckdb");

    public bool HasIndex(string slug) => File.Exists(FilePath(slug));

    /// <summary>Bytes the live index occupies, and zero when there is none: what a refresh has to find room for again.</summary>
    public long LiveSizeBytes(string slug) =>
        File.Exists(FilePath(slug)) ? new FileInfo(FilePath(slug)).Length : 0;

    /// <summary>
    ///     Free bytes on the volume holding the indexes. In the container that is the ephemeral disk
    ///     every project, every clone and the shadow file share, with a ceiling nothing can raise
    ///     (ADR-0003). The volume is found from the path root, which is the drive on Windows and the
    ///     root mount on Linux — the container has one writable filesystem, so that is the same volume.
    /// </summary>
    public long FreeBytes() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_directory))!).AvailableFreeSpace;

    /// <summary>
    ///     A connection bound to the project with <c>USE</c>, attached first if the instance does not hold
    ///     it yet, wrapped in a lease that holds the project open against a swap. Callers dispose it after
    ///     one unit of work and never keep it (ADR-0003). Null when the project has no index yet, which is
    ///     an answer for the caller to phrase, not a failure.
    /// </summary>
    public async Task<IndexLease?> OpenAsync(string slug, CancellationToken cancellationToken)
    {
        var gate = GateFor(slug);
        // Taken before the file is looked for: during the moment of a swap there is no file to find,
        // and a caller arriving then should read the new index rather than be told there is none.
        await gate.EnterAsync(cancellationToken);
        try
        {
            if (!HasIndex(slug))
            {
                gate.Leave();
                return null;
            }

            var connection = await ConnectAsync(cancellationToken);
            try
            {
                await AttachAsync(connection, slug, FilePath(slug), cancellationToken);
                await ExecuteAsync(connection, $"USE {Quote(slug)}", cancellationToken);
                return new IndexLease(connection, gate.Leave);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        catch
        {
            gate.Leave();
            throw;
        }
    }

    /// <summary>
    ///     Creates the shadow index a refresh fills: a second file beside the live one, attached under
    ///     its own catalog and returned with the tables created. The live index is untouched and keeps
    ///     answering every query until <see cref="SwapShadowAsync" /> (CONTEXT.md, ADR-0003).
    /// </summary>
    public async Task<ShadowIndex> CreateShadowAsync(string slug, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(cancellationToken);
        try
        {
            string catalog = ShadowCatalog(slug);
            await _attachGate.WaitAsync(cancellationToken);
            try
            {
                // Whatever a previous refresh left behind is worthless: the shadow is written from
                // scratch every time, and an abandoned one is only a file in the way.
                await ExecuteAsync(connection, $"DETACH DATABASE IF EXISTS {Quote(catalog)}", cancellationToken);
                _attached.Remove(catalog);
                DeleteIndexFile(ShadowPath(slug));
                await ExecuteAsync(connection, $"ATTACH {Literal(ShadowPath(slug))} AS {Quote(catalog)}",
                    cancellationToken);
                _attached.Add(catalog);
            }
            finally
            {
                _attachGate.Release();
            }

            await ExecuteAsync(connection, $"USE {Quote(catalog)}", cancellationToken);
            await ExecuteAsync(connection, Schema, cancellationToken);
            return new ShadowIndex(connection, catalog);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Ends a build: creates the BM25 index once the tables are loaded, when the engine allows, and records the
    ///     build. Returns whether a full-text index exists. There is deliberately no ART index on
    ///     <c>lines(file_id)</c>: rows are appended in file order, so zone maps already prune a file's
    ///     lines to one or two row groups, and an ART index would cost memory and slow the Parquet restore.
    /// </summary>
    public async Task<bool> CompleteBuildAsync(DuckDBConnection connection, bool singleRepository,
        CancellationToken cancellationToken)
    {
        if (FtsAvailable)
            // Tokens are lower-cased identifiers: letters, digits and underscore. No stemming and no stop
            // words, because `Get`, `if` and `id` are exactly what an agent searches code for. Runs inside
            // the attached database because match_bm25 only resolves its tables in the current one.
            await ExecuteAsync(connection, """
                                           PRAGMA create_fts_index('lines', 'line_id', 'content',
                                               stemmer = 'none', stopwords = 'none', ignore = '[^a-z0-9_]+',
                                               lower = 1, strip_accents = 0, overwrite = 1)
                                           """, cancellationToken);

        // Written last, so a row in index_info means the build completed and describes what exists.
        await ExecuteAsync(connection,
            $"INSERT INTO index_info VALUES ({SchemaVersion}, now(), {(FtsAvailable ? "true" : "false")}, {(singleRepository ? "true" : "false")})",
            cancellationToken);
        return FtsAvailable;
    }

    /// <summary>
    ///     Makes the finished shadow index the live one: waits for in-flight queries to finish with a
    ///     hard timeout, detaches both catalogs, replaces the file and lets the next caller attach it.
    ///     New callers arriving mid-swap wait rather than see a project with no index, so a search
    ///     answers completely from the old index or completely from the new one and never from neither.
    ///     The shadow's own connection must be disposed first; the file cannot be moved while open.
    /// </summary>
    public Task SwapShadowAsync(string slug, CancellationToken cancellationToken) =>
        WithoutReadersAsync(slug,
            "the new index was swapped in anyway",
            async connection =>
            {
                string catalog = ShadowCatalog(slug);
                // Both catalogs go first: DETACH is what closes the file handles, and neither file can
                // be deleted or moved while the instance holds one.
                await ExecuteAsync(connection, $"DETACH DATABASE IF EXISTS {Quote(catalog)}", cancellationToken);
                _attached.Remove(catalog);
                await ExecuteAsync(connection, $"DETACH DATABASE IF EXISTS {Quote(slug)}", cancellationToken);
                _attached.Remove(slug);
                // One overwriting move, never delete-then-move: a move that fails after the old file was
                // deleted would leave the project with no index at all, and the caller's cleanup would
                // then take the shadow too. Overwrite replaces the file or leaves it exactly as it was.
                File.Move(ShadowPath(slug), FilePath(slug), true);
                // A clean DETACH checkpoints and removes the WAL, so both of these are for the case
                // where it did not: a stale live WAL would replay the old tail over the new file, and a
                // stale shadow WAL is bytes nothing will read again.
                File.Delete(FilePath(slug) + ".wal");
                File.Delete(ShadowPath(slug) + ".wal");
            }, cancellationToken);

    /// <summary>
    ///     Removes the shadow file after a refresh failed part-way. The live index is untouched and
    ///     still serving, which is the whole point of building beside it rather than in place.
    /// </summary>
    public async Task DiscardShadowAsync(string slug, CancellationToken cancellationToken)
    {
        using var connection = await ConnectAsync(cancellationToken);
        await _attachGate.WaitAsync(cancellationToken);
        try
        {
            string catalog = ShadowCatalog(slug);
            await ExecuteAsync(connection, $"DETACH DATABASE IF EXISTS {Quote(catalog)}", cancellationToken);
            _attached.Remove(catalog);
            DeleteIndexFile(ShadowPath(slug));
        }
        finally
        {
            _attachGate.Release();
        }
    }

    /// <summary>
    ///     Detaches and removes a project's file, when an operator deletes the project. Deleting a
    ///     project that has no index is not an error, so a missing file is not one. Waits for in-flight
    ///     queries the same way a swap does, so a delete cannot pull the file out from under a search.
    /// </summary>
    public Task DiscardAsync(string slug, CancellationToken cancellationToken) =>
        WithoutReadersAsync(slug,
            "the project was deleted anyway",
            async connection =>
            {
                await ExecuteAsync(connection, $"DETACH DATABASE IF EXISTS {Quote(slug)}", cancellationToken);
                _attached.Remove(slug);
                DeleteIndexFile(FilePath(slug));
            }, cancellationToken);

    /// <summary>
    ///     Runs work that replaces or removes a project's file, with as few readers holding it as the
    ///     drain can manage. Both callers need exactly this — a swap and a delete differ only in what
    ///     they do to the file once the readers are out.
    ///     The drain deliberately leaves the gate open: a search arriving while a rebuild is waiting
    ///     must be answered from the old index, which is the whole promise, not held for however long
    ///     the drain runs. Only the file work itself shuts the gate, and that is a detach and a move.
    ///     A lease taken in the moment between the two is the one case a reader is orphaned without the
    ///     drain having timed out; it fails on its next statement exactly as a timed-out one does, which
    ///     ADR-0003 measured and which is why a lease is never held across units of work.
    /// </summary>
    /// <param name="slug">The project whose file is being replaced or removed.</param>
    /// <param name="timedOut">Logged when the drain gave up; it says what happened anyway.</param>
    /// <param name="work">Given a connection of its own, run with the gate shut.</param>
    /// <param name="cancellationToken">Cancels the drain wait and the connection.</param>
    private async Task WithoutReadersAsync(string slug, string timedOut,
        Func<DuckDBConnection, Task> work, CancellationToken cancellationToken)
    {
        var gate = GateFor(slug);
        var drained = gate.Draining();
        if (!drained.IsCompleted)
            // WaitAsync, not a cancelled wait: past the timeout the work goes ahead. An in-flight query
            // finishes correctly against its own snapshot and only its connection is orphaned.
            try
            {
                await drained.WaitAsync(_drainTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Safe to swallow: the drain is a courtesy with a deadline, not a lock. Going ahead is
                // the decision this ticket makes, and the log line is how it is noticed. The slug stays
                // a field of its own rather than being baked into the sentence, so the line can be
                // filtered by project like every other one.
                _logger.LogWarning("A query on project {Project} was still running after {Seconds:0.#}s; {Outcome}",
                    slug, _drainTimeout.TotalSeconds, timedOut);
            }

        gate.Shut();
        try
        {
            using var connection = await ConnectAsync(cancellationToken);
            await _attachGate.WaitAsync(cancellationToken);
            try
            {
                await work(connection);
            }
            finally
            {
                _attachGate.Release();
            }
        }
        finally
        {
            // Reopened even on failure: the old index is still on disk and still correct, so keeping
            // every reader out after a swap that did not happen would turn one failure into an outage.
            gate.Reopen();
        }
    }

    private SwapGate GateFor(string slug)
    {
        lock (_swapGatesSync)
        {
            if (!_swapGates.TryGetValue(slug, out var gate)) _swapGates[slug] = gate = new SwapGate();
            return gate;
        }
    }

    private async Task AttachAsync(DuckDBConnection connection, string catalog, string path,
        CancellationToken cancellationToken)
    {
        await _attachGate.WaitAsync(cancellationToken);
        try
        {
            if (_attached.Contains(catalog)) return;
            await ExecuteAsync(connection, $"ATTACH IF NOT EXISTS {Literal(path)} AS {Quote(catalog)}",
                cancellationToken);
            _attached.Add(catalog);
        }
        finally
        {
            _attachGate.Release();
        }
    }

    private bool TryLoadFts(SearchEngine engine, ILogger logger)
    {
        try
        {
            // INSTALL downloads the extension on first use; #14 bakes it into the image so production
            // never reaches out. Offline, it throws here rather than failing silently later.
            using var command = _anchor.CreateCommand();
            command.CommandText = "INSTALL fts; LOAD fts";
            command.ExecuteNonQuery();
            return true;
        }
        catch (DuckDBException ex) when (engine == SearchEngine.Auto)
        {
            // Safe to swallow: Auto asks for the best available engine, and substring scan is the
            // documented offline fallback (ADR-0004). The log line is how an operator learns of the downgrade.
            logger.LogWarning(ex, "The fts extension is not available; searches fall back to substring scan");
            return false;
        }
        catch (DuckDBException ex)
        {
            throw new InvalidOperationException(
                "Index:SearchEngine is Fts but the fts extension could not be installed or loaded. "
                + "Connect to the network once, bake the extension into the image (#14), or set Substring.", ex);
        }
    }

    private async Task<DuckDBConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new DuckDBConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The database file and the write-ahead log beside it, which outlives it after an unclean stop.</summary>
    private static void DeleteIndexFile(string path)
    {
        File.Delete(path);
        File.Delete(path + ".wal");
    }

    private string ShadowPath(string slug) => Path.Combine(_directory, slug + ".shadow.duckdb");

    /// <summary>
    ///     The catalog the shadow of a project is attached under. A slug is lowercase letters, digits
    ///     and hyphens, so no project can be called this and a shadow can never collide with a live index.
    /// </summary>
    private static string ShadowCatalog(string slug) => slug + "$shadow";

    /// <summary>
    ///     A slug is validated to lowercase letters, digits and hyphens before it reaches here, so quoting
    ///     it as an identifier is safe. The quotes are for the hyphen, which is not an identifier character.
    /// </summary>
    private static string Quote(string slug) => $"\"{slug}\"";

    /// <summary>A file path as a SQL string literal. Paths come from configuration and the slug, never from a request.</summary>
    private static string Literal(string path) => $"'{path.Replace("'", "''")}'";

    /// <summary>
    ///     Lets any number of readers hold a project at once, says when the ones in flight have
    ///     finished, and keeps readers out for the moment the file is replaced or removed — the drain
    ///     ADR-0003 says <c>DETACH</c> will not do for us. Waiting for the readers and shutting the gate
    ///     are deliberately two steps, because a reader arriving during the wait is answered rather than
    ///     blocked. Not a <c>ReaderWriterLockSlim</c>: this is held across awaits and released on
    ///     whichever thread finishes the work, which that type forbids. One writer at a time is the
    ///     caller's guarantee — a refresh holds the server's single rebuild slot before it gets here.
    /// </summary>
    private sealed class SwapGate
    {
        private readonly Lock _sync = new();
        private TaskCompletionSource _drained = Settled();
        private bool _exclusive;
        private int _readers;
        private TaskCompletionSource _reopened = Settled();

        /// <summary>Waits out an exclusive hold in progress, then counts this caller as in flight.</summary>
        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task reopened;
                lock (_sync)
                {
                    if (!_exclusive)
                    {
                        _readers++;
                        return;
                    }

                    reopened = _reopened.Task;
                }

                await reopened.WaitAsync(cancellationToken);
            }
        }

        public void Leave()
        {
            lock (_sync)
                if (--_readers == 0)
                    _drained.TrySetResult();
        }

        /// <summary>
        ///     The task that completes once every reader in flight right now has left. The gate stays
        ///     open, so a reader arriving while the caller waits on this is admitted and answered —
        ///     and, by being admitted, keeps this task pending until it too is done.
        /// </summary>
        public Task Draining()
        {
            lock (_sync)
            {
                if (_readers == 0) return Task.CompletedTask;
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }

        /// <summary>Keeps new readers waiting. Held only for the file work, never for the drain.</summary>
        public void Shut()
        {
            lock (_sync)
            {
                _exclusive = true;
                _reopened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Reopen()
        {
            lock (_sync)
            {
                _exclusive = false;
                _reopened.TrySetResult();
            }
        }

        private static TaskCompletionSource Settled()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
