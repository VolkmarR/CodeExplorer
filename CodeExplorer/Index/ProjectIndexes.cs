using System.Collections.Concurrent;
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
public sealed class IndexLease(DuckDBConnection connection, bool fullTextLoaded, Action<bool> release)
    : IDisposable
{
    private volatile bool _broken;
    private int _released;

    public DuckDBConnection Connection { get; } = connection;

    /// <summary>
    ///     Whether this process can run <c>match_bm25</c> at all. One of the two truths behind a
    ///     full-text search; the other, whether this file holds a BM25 index, is in its
    ///     <c>index_info</c>, and <c>IndexReader.HasFullTextAsync</c> is where they meet.
    /// </summary>
    public bool FullTextLoaded { get; } = fullTextLoaded;

    /// <summary>
    ///     Says this connection must not be handed to anyone else: the work on it threw or was
    ///     cancelled, and a statement that ended that way can leave a result part-read behind it. Every
    ///     connection used to be closed after one call, so pooling is what makes this need saying, and
    ///     <see cref="IndexReaders.OverIndexAsync{T}" /> is the one seam that says it.
    /// </summary>
    public void Broken() => _broken = true;

    public void Dispose()
    {
        // The connection is not closed here: unless it was reported broken it goes back to its
        // project's pool, and releasing is what puts it there and only then lets a swap through
        // (#149). The order is the point — a connection handed back after the drain had counted this
        // reader out would land in a pool the swap had already emptied, and the next caller would get
        // a binding to a detached catalog.
        // Guarded, because a double dispose would let a swap through while another lease still holds
        // the project — the one thing the drain exists to prevent.
        if (Interlocked.Exchange(ref _released, 1) == 0) release(!_broken);
    }
}

/// <summary>
///     The shadow index a refresh fills (CONTEXT.md): a connection already <c>USE</c>ing the shadow
///     file, and the catalog name an appender targets. It is a second file next to the live one, so
///     the live index keeps answering queries until <see cref="ProjectIndexes.SwapShadowAsync" />.
/// </summary>
public sealed class ShadowIndex(DuckDBConnection connection, string catalog, string slug, bool fullTextLoaded)
    : IDisposable
{
    public DuckDBConnection Connection { get; } = connection;

    /// <summary>What <c>CreateAppender</c> is given; it is not the project slug, so it is passed rather than derived.</summary>
    public string Catalog { get; } = catalog;

    /// <summary>
    ///     Which project is being rebuilt. Carried here rather than passed alongside, so that a build
    ///     cannot be told to tag its telemetry with one project while writing another's file.
    /// </summary>
    public string Slug { get; } = slug;

    /// <summary>
    ///     Ends a build once the tables are loaded: creates the BM25 index when this process has the
    ///     extension, and records the build. The <c>index_info</c> row is written last, so a row means
    ///     the build completed and describes what exists. There is deliberately no ART index on
    ///     <c>lines(file_id)</c>: rows are appended in file order, so zone maps already prune a file's
    ///     lines to one or two row groups, and an ART index would cost memory and slow the Parquet restore.
    /// </summary>
    /// <param name="singleRepository">How this project names its files (ADR-0006), recorded in the index.</param>
    /// <param name="report">
    ///     How far the build has got. The BM25 build is the longest thing in a refresh on a large
    ///     project and was reported under the attribution's label until #91; the phase is announced from
    ///     here rather than by the caller because only the shadow knows whether there is one to build.
    /// </param>
    /// <param name="cancellationToken">Cancelling between the two statements leaves a shadow with no row, which is a shadow to discard.</param>
    public async Task CompleteAsync(bool singleRepository, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        if (fullTextLoaded)
        {
            report(new RefreshProgress(RefreshProgress.FullTextStep, RefreshProgress.TotalStepCount,
                RefreshProgress.FullTextPhase));
            await FtsExtension.CreateIndexAsync(Connection, cancellationToken);
        }

        using var command = Connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO index_info VALUES ({ProjectIndexes.SchemaVersion}, now(), {(fullTextLoaded ? "true" : "false")}, {(singleRepository ? "true" : "false")})";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose() => Connection.Dispose();
}

/// <summary>
///     What a shadow index would need on disk against what is free. <see cref="Enough" /> is the
///     answer; the two numbers are there so a refusal can name them.
/// </summary>
public readonly record struct DiskRoom(long Free, long Required)
{
    public bool Enough => Free >= Required;
}

/// <summary>
///     The one DuckDB instance every project index is attached to (ADR-0003). Owns the attach state,
///     hands out connections already bound with <c>USE</c>, and creates the shadow file a refresh
///     fills and then swaps in. Nothing else opens a project file.
/// </summary>
public sealed partial class ProjectIndexes : IDisposable
{
    /// <summary>
    ///     The tables a new shadow inherits from the live index instead of rebuilding. They are the
    ///     append-only ones (ADR-0007); everything else is a function of the clone and is written afresh.
    /// </summary>
    private static readonly string[] HistoryTables = ["commits", "commit_files", "attribution"];

    /// <summary>
    ///     Default for <c>Index:DrainSeconds</c>: how long a swap waits for in-flight queries before
    ///     detaching anyway. Long enough that an ordinary grep or file read finishes first, short enough
    ///     that one abandoned query cannot hold a refresh open indefinitely. Past it the swap proceeds
    ///     and the straggler fails on its next statement, which ADR-0003 established is all
    ///     <c>DETACH</c> offers: it never blocks, so the wait is entirely ours and so is its limit.
    /// </summary>
    private const int DefaultDrainSeconds = 30;

    // Attached databases and loaded extensions belong to the instance, and DuckDB.NET disposes the
    // instance once its last connection closes. This connection is never used for queries; it only
    // keeps the instance, and with it every ATTACH and the LOAD below, alive for the process.
    private readonly DuckDBConnection _anchor;

    // ATTACH is instance-wide, so two connections attaching the same slug at once race on the same
    // file. The gate serialises attach and detach; the set remembers what the instance already holds.
    // The set is concurrent so that "is this already attached" can be asked without taking the gate,
    // which is what stopped two reads of two different projects serialising on it (#149). It is still
    // only written under the gate, so the ordering rule the gate exists for is unchanged.
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _attached = new(StringComparer.Ordinal);

    // Whether the file each project's catalog is attached to was written by this build's schema,
    // remembered so that the question costs one query per attach rather than one per lease. It is
    // forgotten with the attach, in DetachAsync, which every replacement of the live file goes through.
    private readonly ConcurrentDictionary<string, bool> _readable = new(StringComparer.Ordinal);

    // One pool per project, because a pooled connection is handed back still bound to that project and
    // USE is what rebinds it on the way out. Kept for the life of the process like the gates beside it.
    private readonly ConcurrentDictionary<string, ConnectionPool> _pools = new(StringComparer.Ordinal);
    private readonly string _connectionString;

    private readonly DurableIndex _durable;

    private readonly string _directory;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger<ProjectIndexes> _logger;

    // One gate per project, like the swap gates and kept for the same reason. It is what makes "one
    // writer at a time" a guarantee of this class rather than of its callers: a swap, a delete and a
    // restore all replace the same file, and a restore does not hold the server's single rebuild slot
    // the way a refresh does. Per project and not one shared gate, because a wake that restores a
    // large index must not hold up a swap of a small one.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writerGates = new(StringComparer.Ordinal);

    // One gate per project, kept for the life of the process: the count is bounded by the control
    // database, and a gate holds nothing but a reader count.
    private readonly ConcurrentDictionary<string, SwapGate> _swapGates = new(StringComparer.Ordinal);

    public ProjectIndexes(IConfiguration configuration, DurableIndex durable, ILogger<ProjectIndexes> logger)
    {
        _durable = durable;
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
        // Before the load below, because that is what the directory is for: in the container it holds
        // the copy the image build put there, so no search waits on a download that cannot happen.
        FtsExtension.UseDirectory(_anchor, configuration["Index:ExtensionDirectory"]);

        FtsAvailable = FtsExtension.TryLoad(_anchor, configuration.GetValue("Index:SearchEngine", SearchEngine.Auto),
            logger);
    }

    /// <summary>
    ///     True when the <c>fts</c> extension is loaded and builds create a BM25 index. False means
    ///     every search is a substring scan, which ranks differently; tests pin the engine for that reason.
    /// </summary>
    public bool FtsAvailable { get; }

    public void Dispose()
    {
        // The pools before the anchor: the anchor is what keeps the instance alive, and a pooled
        // connection outliving it would be a connection on an instance that is tearing itself down.
        foreach (var pool in _pools.Values) pool.Discard();
        _anchor.Dispose();
        _attachGate.Dispose();
    }

    public string FilePath(string slug) => Path.Combine(_directory, slug + ".duckdb");

    public bool HasIndex(string slug) => File.Exists(FilePath(slug));

    /// <summary>
    ///     Whether a shadow index for the project fits on the volume holding the indexes, and the two
    ///     numbers behind the answer. The shadow is a second copy of the whole project, so it needs room
    ///     for one more of what the live index occupies, and <c>create_fts_index</c> needs working space
    ///     on top; twice the live size is the judgement. A project with no index yet has nothing to scale
    ///     from, so <paramref name="floor" /> stands in, and it is the least any refresh is granted.
    ///     In the container the volume is the ephemeral disk every project, every clone and the shadow
    ///     file share, with a ceiling nothing can raise (ADR-0003). It is found from the path root,
    ///     which is the drive on Windows and the root mount on Linux — the container has one writable
    ///     filesystem, so that is the same volume.
    /// </summary>
    /// <param name="slug">The project a refresh would rebuild.</param>
    /// <param name="floor">The least free space a refresh is granted, set by the caller's configuration.</param>
    public DiskRoom RoomForShadow(string slug, long floor)
    {
        // One FileInfo answers both questions; File.Exists followed by a second FileInfo stats twice.
        var file = new FileInfo(FilePath(slug));
        long live = file.Exists ? file.Length : 0;
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_directory))!).AvailableFreeSpace;
        return new DiskRoom(free, Math.Max(floor, live * 2));
    }

    /// <summary>
    ///     A connection bound to the project with <c>USE</c>, attached first if the instance does not hold
    ///     it yet, wrapped in a lease that holds the project open against a swap. Callers dispose it after
    ///     one unit of work and never keep it (ADR-0003). Null when the project has no index yet, which is
    ///     an answer for the caller to phrase, not a failure.
    /// </summary>
    public async Task<IndexLease?> OpenAsync(string slug, CancellationToken cancellationToken)
    {
        // Timed here and not at the twenty-odd callers, so that every way of asking for a lease is
        // measured and a new one cannot forget to be. It is the first candidate #90 names for the ~1.2 s
        // every call costs regardless of the work it does: a connection is opened per lease and the
        // ATTACH and USE run each time, and none of that was under any instrument.
        using var recording = Telemetry.Lease(slug);

        // Lazily, and here rather than at startup: a replica that scaled to zero has an empty disk, and
        // an off-hours wake should cost the restore of the one project being connected to rather than
        // everyone's (#9). Projects attach on first connection for the same reason, which is what the
        // rest of this method has always done.
        if (!HasIndex(slug)) await RestoreAsync(slug, cancellationToken);

        var lease = await AttachAndLeaseAsync(slug, cancellationToken);
        if (lease is null) recording.Absent();
        else recording.Opened();
        return lease;
    }

    /// <summary>
    ///     A lease on a project that is already on disk, and null when it is not — where
    ///     <see cref="OpenAsync" /> would restore it from the durable copy first. It is what a read
    ///     about a project rather than of it uses: the operator's project list touches every project at
    ///     once, and restoring all of them is the cost lazy attach exists to avoid.
    /// </summary>
    public Task<IndexLease?> PeekAsync(string slug, CancellationToken cancellationToken) =>
        AttachAndLeaseAsync(slug, cancellationToken);

    private async Task<IndexLease?> AttachAndLeaseAsync(string slug, CancellationToken cancellationToken)
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

            // Opening a connection is most of what a lease used to cost. One is borrowed instead, and
            // bound again whatever it was last bound to, which is what CODING_STANDARDS asks for: what
            // may not be reused is the binding, not the socket. The generation is read with it, so a
            // connection borrowed before a swap is thrown away rather than handed back into a pool the
            // swap has emptied.
            var pool = PoolFor(slug);
            var connection = pool.Rent(out int generation) ?? await ConnectAsync(cancellationToken);
            try
            {
                await BindAsync(connection, slug, cancellationToken);

                // A file an older schema wrote is not readable by this build, and every read of it
                // would fail on whatever column the bump added — a Binder Error out of the middle of
                // a query, which is an exception where CODING_STANDARDS asks for an answer (#164).
                // Refused here rather than at each reader, for the reason the lease is taken here.
                if (!await ReadableAsync(connection, slug, cancellationToken))
                {
                    pool.Return(connection, generation);
                    gate.Leave();
                    return null;
                }

                return new IndexLease(connection, FtsAvailable, reusable =>
                {
                    if (reusable) pool.Return(connection, generation);
                    // Closed rather than pooled: the reader said the work on it threw, and the next
                    // borrower must not inherit whatever that left.
                    else connection.Dispose();
                    gate.Leave();
                });
            }
            catch
            {
                // Closed rather than pooled: a connection that failed its ATTACH or its USE is one
                // nothing here can say anything about.
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
    ///     Rebuilds a project's file from its durable copy, and answers whether there was one. The
    ///     Parquet is loaded into a file of its own and that file is moved into place, so a restore
    ///     racing a first refresh cannot have the swap replace the file it is still writing — the move
    ///     goes through the same drain a swap does, and is the same one-file overwrite.
    ///     One project at a time and only that project: the gate is per project, so a wake that restores
    ///     a large index does not hold up a connection to a small one.
    /// </summary>
    private async Task<bool> RestoreAsync(string slug, CancellationToken cancellationToken)
    {
        var gate = WriterGateFor(slug);
        // Held from here to the end, not just around the file work: a swap that started while this was
        // downloading would otherwise install the newer index and have the move below overwrite it with
        // the older durable copy. Holding the writer gate across the whole restore is what makes the
        // recheck below decisive rather than a guess about what happens next.
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have restored it, or a refresh may have swapped one in, while this one
            // waited. Either way there is now an index and nothing to restore.
            if (HasIndex(slug)) return true;

            // Null is a project that has never been indexed, or a copy an older schema wrote. Both
            // mean "rebuild from git", and both have recorded themselves on the way out.
            using var copy = await _durable.FetchAsync(slug, cancellationToken);
            if (copy is null) return false;

            string path = RestorePath(slug);
            string catalog = RestoreCatalog(slug);
            using (var connection = await ConnectAsync(cancellationToken))
            {
                await AttachEmptyAsync(connection, catalog, path, cancellationToken);
                await _durable.LoadAsync(connection, copy, FtsAvailable, cancellationToken);
            }

            // ReplaceFileAsync and not WithoutReadersAsync: the writer gate is already held, and taking
            // it twice would deadlock on a semaphore that is deliberately not reentrant.
            await ReplaceFileAsync(slug, "the restored index was put in place anyway", async connection =>
            {
                await DetachAsync(connection, catalog, cancellationToken);
                await DetachAsync(connection, slug, cancellationToken);
                File.Move(path, FilePath(slug), true);
                File.Delete(FilePath(slug) + ".wal");
                File.Delete(path + ".wal");
            }, cancellationToken);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Restored project {Project} from its durable copy", slug);
            return true;
        }
        finally
        {
            gate.Release();
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
            await AttachEmptyAsync(connection, catalog, ShadowPath(slug), cancellationToken);
            await CarryHistoryAsync(connection, slug, cancellationToken);
            return new ShadowIndex(connection, catalog, slug, FtsAvailable);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Copies the live index's history into the fresh shadow, so a build appends to it rather than
    ///     walking every commit again (ADR-0007). History is append-only, which is what makes this sound:
    ///     a commit already recorded cannot change, so carrying it over is not a cache that can go stale.
    ///     Done here rather than by the caller, so that "a shadow starts out knowing what the live index
    ///     knew" is a property of creating one and not of a caller remembering to ask. The build prunes
    ///     what no longer belongs — a repository since removed — because only it knows what was read.
    ///     Nothing to carry is the ordinary case for a first build. A live file at the current version
    ///     holds every history table: the schema creates them all in one statement and the
    ///     <c>index_info</c> row that says which version it is was written last, so once the version
    ///     matches there is no table left to probe for.
    ///     An older <see cref="SchemaVersion" /> carries nothing at all. The durable copy is already
    ///     refused on that test, and the live file on disk needs the same one for the same reason: these
    ///     tables are copied column for column, so a history table that gained a column would be
    ///     inserted short — and where the shapes did happen to match, the carried rows would be blind to
    ///     whatever the new column records, which is the quiet wrong answer a version bump exists to
    ///     prevent (#131). Carrying nothing costs one full re-walk per project, once.
    /// </summary>
    private async Task CarryHistoryAsync(DuckDBConnection connection, string slug,
        CancellationToken cancellationToken)
    {
        if (!HasIndex(slug)) return;
        await AttachAsync(connection, slug, FilePath(slug), cancellationToken);
        if (!await LiveSchemaMatchesAsync(connection, slug, cancellationToken)) return;

        foreach (string table in HistoryTables)
            await connection.ExecuteAsync(
                $"INSERT INTO {table} SELECT * FROM {Quote(slug)}.main.{table}", cancellationToken);
    }

    /// <summary>
    ///     Whether the attached live index was written by this build's schema. Anything else reads as
    ///     "no", which is the safe direction in every case it covers — an interrupted build that wrote
    ///     the tables and never the <c>index_info</c> row, a file with no tables at all, an older
    ///     version: a re-walk is slow, and a carry-over from a shape this build does not know is either
    ///     a failed refresh or a quietly wrong answer. The table is probed for rather than the failure
    ///     caught, so a real error still throws.
    /// </summary>
    private static async Task<bool> LiveSchemaMatchesAsync(DuckDBConnection connection, string slug,
        CancellationToken cancellationToken)
    {
        if (await connection.CountAsync(
                "SELECT count(*) FROM duckdb_tables() "
                + "WHERE database_name = $slug AND schema_name = 'main' AND table_name = 'index_info'",
                [new DuckDBParameter("slug", slug)], cancellationToken) == 0)
            return false;

        using var version = connection.CreateCommand();
        version.CommandText = $"SELECT max(schema_version) FROM {Quote(slug)}.main.index_info";
        return await version.ExecuteScalarAsync(cancellationToken) is int found && found == SchemaVersion;
    }

    /// <summary>
    ///     Whether the project's attached file can be read by this build, remembered per attach. The
    ///     same test the carry-over makes, asked on the way in rather than on the way out: a version
    ///     bump adds a column, and a reader handed the older file fails on the first query naming it.
    ///     Answered once per attach because a lease is the hot path (#149) and the file cannot change
    ///     underneath an attach — everything that replaces it detaches the catalog first.
    /// </summary>
    private async Task<bool> ReadableAsync(DuckDBConnection connection, string slug,
        CancellationToken cancellationToken)
    {
        if (_readable.TryGetValue(slug, out bool known)) return known;

        bool current = await LiveSchemaMatchesAsync(connection, slug, cancellationToken);
        _readable[slug] = current;
        if (!current)
            _logger.LogWarning(
                "Project {Project} has an index this build cannot read: it was written for a schema "
                + "other than version {Version}, and only a refresh rewrites it", slug, SchemaVersion);
        return current;
    }

    /// <summary>
    ///     Whether the project's index is on disk but written for another schema version, which is the
    ///     one reason <see cref="OpenAsync" /> refuses a project that does have a file. It is what lets
    ///     a reader say which of the two refusals it is looking at without asking the file again.
    /// </summary>
    public bool SchemaOutdated(string slug) => _readable.TryGetValue(slug, out bool current) && !current;

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
                // Both catalogs go first: DETACH is what closes the file handles, and neither file can
                // be deleted or moved while the instance holds one.
                await DetachAsync(connection, ShadowCatalog(slug), cancellationToken);
                await DetachAsync(connection, slug, cancellationToken);
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
        // No drain: nothing reads a shadow, so there is nobody to wait for. This is the one attach-gate
        // caller that is not replacing what the readers are using.
        await UnderAttachGateAsync(async () =>
        {
            await DetachAsync(connection, ShadowCatalog(slug), cancellationToken);
            DeleteIndexFile(ShadowPath(slug));
        }, cancellationToken);
    }

    /// <summary>
    ///     Detaches and removes a project's file, when an operator deletes the project. Deleting a
    ///     project that has no index is not an error, so a missing file is not one. Waits for in-flight
    ///     queries the same way a swap does, so a delete cannot pull the file out from under a search.
    /// </summary>
    public async Task DiscardAsync(string slug, CancellationToken cancellationToken)
    {
        await WithoutReadersAsync(slug,
            "the project was deleted anyway",
            async connection =>
            {
                await DetachAsync(connection, slug, cancellationToken);
                DeleteIndexFile(FilePath(slug));
            }, cancellationToken);

        // The pool goes too, and not only its connections: the swap above already closed those, and
        // this is the one thing here a project can be finished with. Its gate stays, because a gate
        // holds a reader count somebody may still be waiting on; a pool holds nothing once emptied.
        if (_pools.TryRemove(slug, out var pool)) pool.Discard();

        // The durable copy goes with it. Left behind, it would restore a deleted project's files the
        // first time someone connected to a project that reused the slug — the one case where the
        // durable copy outliving the disk is wrong.
        await _durable.RemoveAsync(slug, cancellationToken);
    }

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
        await WriterGateFor(slug).WaitAsync(cancellationToken);
        try
        {
            await ReplaceFileAsync(slug, timedOut, work, cancellationToken);
        }
        finally
        {
            WriterGateFor(slug).Release();
        }
    }

    /// <summary>
    ///     The drain, the shut gate and the file work, with the project's writer gate assumed to be held
    ///     already. Separate from <see cref="WithoutReadersAsync" /> for the one caller that has to hold
    ///     that gate across more than the file work: a restore holds it from the download onwards, so
    ///     that nothing swaps a newer index in underneath the older one it is about to move into place.
    /// </summary>
    private async Task ReplaceFileAsync(string slug, string timedOut,
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
        // Every idle connection of this project is closed, and the ones still out are marked not to
        // come back: the file below is about to be detached, and a pooled connection bound to a
        // catalog that no longer exists is exactly the "Catalog does not exist" ADR-0003 describes,
        // handed to whoever borrowed it next. Done after the gate is shut, so nothing can borrow one
        // between here and the detach, and after the drain, so this closes connections nobody holds.
        PoolFor(slug).Discard();
        try
        {
            using var connection = await ConnectAsync(cancellationToken);
            await UnderAttachGateAsync(() => work(connection), cancellationToken);
        }
        finally
        {
            // Reopened even on failure: the old index is still on disk and still correct, so keeping
            // every reader out after a swap that did not happen would turn one failure into an outage.
            gate.Reopen();
        }
    }

    /// <summary>
    ///     Runs work that attaches or detaches, with the instance-wide <c>ATTACH</c> state to itself.
    ///     Every caller that touches <c>_attached</c> goes through here, so the one ordering rule this
    ///     class has is written once.
    /// </summary>
    private async Task UnderAttachGateAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        await _attachGate.WaitAsync(cancellationToken);
        try
        {
            await work();
        }
        finally
        {
            _attachGate.Release();
        }
    }

    /// <summary>
    ///     The gate for a project, created on first use. Kept afterwards: the count is bounded by the
    ///     control database, a gate holds nothing but a reader count, and dropping one while a reader
    ///     waits on it would let the next caller past a hold that has not ended. A deleted project's
    ///     gate is the cost of that, and it is a few bytes. Taken on every lease, so without a lock: two
    ///     first callers may each build one, but <c>GetOrAdd</c> hands both the same winner, and the
    ///     loser is dropped before anyone has entered it.
    /// </summary>
    private SwapGate GateFor(string slug) => _swapGates.GetOrAdd(slug, _ => new SwapGate());

    /// <summary>The project's pool, created on first use and kept for the same reason its gate is.</summary>
    private ConnectionPool PoolFor(string slug) => _pools.GetOrAdd(slug, _ => new ConnectionPool());

    /// <summary>
    ///     Attaches the project if the instance does not hold it, and binds this connection to it.
    ///     The retry is for the window ADR-0003 already describes and the fast path made sharper: a
    ///     lease taken between the drain and the shut gate, or one the drain timed out on, can read
    ///     the catalog as attached and then find the swap has detached it. Before the fast path that
    ///     caller queued on the attach gate behind the swap and came out the other side attached to
    ///     the file that replaced it; skipping the gate is what turned that into a failure.
    ///     So it is retried once, through the gate, which is the same wait it used to do. Only when
    ///     the catalog really has gone — anything else is this caller's own error and is rethrown
    ///     untouched. A second failure means the project is being replaced faster than a lease can be
    ///     taken, and that throws, because at that point there is nothing to read.
    /// </summary>
    private async Task BindAsync(DuckDBConnection connection, string slug, CancellationToken cancellationToken)
    {
        try
        {
            await AttachAndUse();
        }
        catch (DuckDBException) when (!_attached.ContainsKey(slug) && !cancellationToken.IsCancellationRequested)
        {
            await AttachAndUse();
        }

        async Task AttachAndUse()
        {
            await AttachAsync(connection, slug, FilePath(slug), cancellationToken);
            await connection.ExecuteAsync($"USE {Quote(slug)}", cancellationToken);
        }
    }

    /// <summary>
    ///     Attaches a catalog unless the instance already holds it. The check is made twice: once
    ///     without the gate, because a read of an attached project is the ordinary case and taking a
    ///     process-wide semaphore for it made every project's reads queue behind every other project's
    ///     (#149), and once inside, because two callers can arrive at an unattached catalog together.
    ///     Reading the set outside the gate is safe for the one thing this asks of it. A catalog is
    ///     removed only by a detach, and every detach of a project's own catalog runs with that
    ///     project's swap gate shut — which the caller of this is holding open. So "attached" cannot
    ///     turn false underneath a reader, and a stale "not attached" only costs the gate it would
    ///     have taken anyway.
    /// </summary>
    private Task AttachAsync(DuckDBConnection connection, string catalog, string path,
        CancellationToken cancellationToken) =>
        _attached.ContainsKey(catalog)
            ? Task.CompletedTask
            : Gated(catalog, connection, path, cancellationToken);

    private Task Gated(string catalog, DuckDBConnection connection, string path,
        CancellationToken cancellationToken)
    {
        // Recorded before the wait and not after it: what this counts is how often the gate is
        // reached at all, and a caller that then waited on it is the contention the count is read for.
        Telemetry.AttachGated(catalog);
        return UnderAttachGateAsync(async () =>
        {
            if (_attached.ContainsKey(catalog)) return;
            await connection.ExecuteAsync($"ATTACH IF NOT EXISTS {IndexQuery.Literal(path)} AS {Quote(catalog)}",
                cancellationToken);
            _attached[catalog] = 0;
        }, cancellationToken);
    }

    /// <summary>
    ///     Detaches a catalog and forgets what was remembered about it, so the next attach asks again.
    ///     The caller holds the attach gate, which is not reentrant.
    /// </summary>
    private async Task DetachAsync(DuckDBConnection connection, string catalog,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync($"DETACH DATABASE IF EXISTS {Quote(catalog)}", cancellationToken);
        _attached.TryRemove(catalog, out _);
        _readable.TryRemove(catalog, out _);
    }

    /// <summary>
    ///     Attaches an empty file under a catalog of its own and binds the connection to it with the
    ///     tables created: how a restore and a shadow both start. Whatever an abandoned one of either
    ///     left behind is worthless, since the file is written from scratch every time, so it is
    ///     detached and deleted rather than reused.
    /// </summary>
    private async Task AttachEmptyAsync(DuckDBConnection connection, string catalog, string path,
        CancellationToken cancellationToken)
    {
        await UnderAttachGateAsync(async () =>
        {
            await DetachAsync(connection, catalog, cancellationToken);
            DeleteIndexFile(path);
            await connection.ExecuteAsync($"ATTACH {IndexQuery.Literal(path)} AS {Quote(catalog)}",
                cancellationToken);
            _attached[catalog] = 0;
        }, cancellationToken);

        await connection.ExecuteAsync($"USE {Quote(catalog)}", cancellationToken);
        await connection.ExecuteAsync(Schema, cancellationToken);
    }

    private async Task<DuckDBConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new DuckDBConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>The database file and the write-ahead log beside it, which outlives it after an unclean stop.</summary>
    private static void DeleteIndexFile(string path)
    {
        File.Delete(path);
        File.Delete(path + ".wal");
    }

    private string ShadowPath(string slug) => Path.Combine(_directory, slug + ".shadow.duckdb");

    private string RestorePath(string slug) => Path.Combine(_directory, slug + ".restore.duckdb");

    /// <summary>The catalog a restore fills, kept apart from the live one the way a shadow's is.</summary>
    private static string RestoreCatalog(string slug) => slug + "$restore";

    /// <summary>
    ///     The gate that lets one writer of a project run at a time, created on first use. A semaphore
    ///     that loses the <c>GetOrAdd</c> race was never waited on, so dropping it undisposed holds nothing.
    /// </summary>
    private SemaphoreSlim WriterGateFor(string slug) => _writerGates.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));

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

    /// <summary>
    ///     One project's idle connections. Opening a DuckDB connection and binding it was most of what
    ///     a lease cost, and every read opened a new one (#149); a lease borrows one instead and hands
    ///     it back, with <c>USE</c> re-run on every checkout so nothing is ever read through a binding
    ///     it did not just make.
    ///     The generation is what makes handing one back safe. A swap detaches the catalog
    ///     these are bound to, so it calls <see cref="Discard" />; a connection borrowed before that —
    ///     which the drain's timeout allows — comes back carrying the older generation and is closed
    ///     instead of pooled. Without it, one orphaned reader would poison the pool for everyone after
    ///     the swap.
    /// </summary>
    private sealed class ConnectionPool
    {
        /// <summary>
        ///     Idle connections kept per project. Four, because a replica has two cores (ADR-0003) and
        ///     a read is mostly engine work: past a handful, the connections are not being reused,
        ///     they are being held. A burst beyond it is served and its connections closed on the way
        ///     back rather than refused.
        /// </summary>
        private const int MaxIdle = 4;

        private readonly ConcurrentBag<DuckDBConnection> _idle = [];
        private int _generation;

        // Counted rather than asked of the bag: ConcurrentBag.Count walks its per-thread queues and
        // takes their locks, and this is read on the way out of every lease.
        private int _idleCount;

        /// <summary>
        ///     An idle connection and the generation it belongs to, read together so that what is
        ///     handed back is weighed against the state the pool was in when it was handed out.
        /// </summary>
        public DuckDBConnection? Rent(out int generation)
        {
            generation = Volatile.Read(ref _generation);
            if (!_idle.TryTake(out var connection)) return null;
            Interlocked.Decrement(ref _idleCount);
            return connection;
        }

        public void Return(DuckDBConnection connection, int generation)
        {
            if (generation != Volatile.Read(ref _generation) || Volatile.Read(ref _idleCount) >= MaxIdle)
            {
                connection.Dispose();
                return;
            }

            Interlocked.Increment(ref _idleCount);
            _idle.Add(connection);
        }

        /// <summary>
        ///     Closes every idle connection and marks the ones still out as not to be returned. Called
        ///     where the project's catalog is about to be detached, and on shutdown.
        /// </summary>
        public void Discard()
        {
            Interlocked.Increment(ref _generation);
            while (_idle.TryTake(out var connection))
            {
                Interlocked.Decrement(ref _idleCount);
                connection.Dispose();
            }
        }
    }

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

        // Null is the ordinary state of both: nobody is waiting to be told the readers have gone, and
        // the gate is open. A source exists exactly while someone is waiting on what it reports, so
        // "shut" and "someone wants the drain" need no flag beside them.
        private TaskCompletionSource? _drained;
        private int _readers;
        private TaskCompletionSource? _reopened;

        /// <summary>Waits out an exclusive hold in progress, then counts this caller as in flight.</summary>
        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task reopened;
                lock (_sync)
                {
                    if (_reopened is null)
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
                    _drained?.TrySetResult();
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
            lock (_sync) _reopened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Reopen()
        {
            lock (_sync)
            {
                _reopened?.TrySetResult();
                _reopened = null;
            }
        }
    }
}
