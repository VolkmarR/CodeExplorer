using System.Collections.Concurrent;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Index;

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
    private static readonly string[] _historyTables = ["commits", "commit_files", "attribution"];

    /// <summary>
    ///     Default for <c>Index:DrainSeconds</c>: how long a swap waits for in-flight queries before
    ///     detaching anyway. Long enough that an ordinary grep or file read finishes first, short enough
    ///     that one abandoned query cannot hold a refresh open indefinitely. Past it the swap proceeds
    ///     and the straggler fails on its next statement, which ADR-0003 established is all
    ///     <c>DETACH</c> offers: it never blocks, so the wait is entirely ours and so is its limit.
    /// </summary>
    private const int _defaultDrainSeconds = 30;

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

    // How many times each project's index has been discarded, written only under its writer gate. See
    // DiscardCount; kept for the life of the process like the gates, and bounded the same way.
    private readonly ConcurrentDictionary<string, long> _discards = new(StringComparer.Ordinal);

    // The projects whose live index a refresh restored without its BM25 index and has not yet swapped
    // out (#290); see SettleRestoreAsync. Written under the writer gate, like the count above.
    private readonly ConcurrentDictionary<string, byte> _withoutFullText = new(StringComparer.Ordinal);

    // One gate per project, kept for the life of the process: the count is bounded by the control
    // database, and a gate holds nothing but a reader count.
    private readonly ConcurrentDictionary<string, SwapGate> _swapGates = new(StringComparer.Ordinal);

    public ProjectIndexes(IConfiguration configuration, DurableIndex durable, ILogger<ProjectIndexes> logger)
    {
        _durable = durable;
        _logger = logger;
        _directory = Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "indexes");
        Directory.CreateDirectory(_directory);
        _drainTimeout = TimeSpan.FromSeconds(configuration.GetValue("Index:DrainSeconds", _defaultDrainSeconds));
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
    ///     file share, with a ceiling nothing can raise (ADR-0003). It is the filesystem that holds the
    ///     index folder (<see cref="FreeSpace.Available" />), and no longer the path root, which on Linux
    ///     is always <c>/</c> and measured the image's own filesystem wherever the data directory is a
    ///     mounted volume.
    /// </summary>
    /// <param name="slug">The project a refresh would rebuild.</param>
    /// <param name="floor">The least free space a refresh is granted, set by the caller's configuration.</param>
    public DiskRoom RoomForShadow(string slug, long floor)
    {
        // One FileInfo answers both questions; File.Exists followed by a second FileInfo stats twice.
        var file = new FileInfo(FilePath(slug));
        long live = file.Exists ? file.Length : 0;
        long free = FreeSpace.Available(_directory);
        return new DiskRoom(free, Math.Max(floor, live * 2));
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

        await using var version = connection.CreateCommand();
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
    ///     Detaches and removes a project's file, when an operator deletes the project. Deleting a
    ///     project that has no index is not an error, so a missing file is not one. Waits for in-flight
    ///     queries the same way a swap does, so a delete cannot pull the file out from under a search.
    /// </summary>
    public async Task DiscardAsync(string slug, CancellationToken cancellationToken)
    {
        // The whole discard is one hold of the writer gate, the durable copy included. Released after
        // the file and before the copy, as it once was, a restore waiting on the gate — an agent's first
        // open, a warm-up that listed the project before the delete — found no file, took the gate and
        // loaded the copy that had not been removed yet, putting the deleted project back
        // (GHSA-253f-grfp-cqq7).
        using (await HoldWriterAsync(slug, cancellationToken))
        {
            // Counted before anything is removed, so a refresh waiting on the gate to publish sees it
            // whether or not the rest of this completes.
            _discards.AddOrUpdate(slug, 1, (_, count) => count + 1);
            _withoutFullText.TryRemove(slug, out _);

            await ReplaceFileAsync(slug, "the project was deleted anyway", async connection =>
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
    }

    /// <summary>
    ///     Waits for the project's writer gate and holds it until disposed. Every caller holds it across
    ///     more than the file work: a restore from the download onwards, so that nothing swaps a newer
    ///     index in underneath the older one it is about to move into place; a publish across the check
    ///     and the durable store; a discard across the durable copy's removal.
    /// </summary>
    private async Task<WriterHold> HoldWriterAsync(string slug, CancellationToken cancellationToken)
    {
        var gate = WriterGateFor(slug);
        await gate.WaitAsync(cancellationToken);
        return new WriterHold(gate);
    }

    private readonly struct WriterHold(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>
    ///     Runs work that replaces or removes a project's file, with as few readers holding it as the
    ///     drain can manage, by a caller already holding the project's writer gate.
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
            await using var connection = await ConnectAsync(cancellationToken);
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
    ///     The caller holds the attach gate, which is not reentrant. Forgotten even when the statement
    ///     throws: a DETACH whose checkpoint failed has detached all the same and says so in its error,
    ///     and remembering a catalog that is gone would skip the next attach. One that really is still
    ///     attached costs only the <c>ATTACH IF NOT EXISTS</c> the next caller then runs.
    /// </summary>
    private async Task DetachAsync(DuckDBConnection connection, string catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync($"DETACH DATABASE IF EXISTS {Quote(catalog)}", cancellationToken);
        }
        finally
        {
            _attached.TryRemove(catalog, out _);
            _readable.TryRemove(catalog, out _);
        }
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
        await connection.ExecuteAsync(_schema, cancellationToken);
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

    /// <summary>
    ///     The gate that lets one writer of a project run at a time, created on first use. A semaphore
    ///     that loses the <c>GetOrAdd</c> race was never waited on, so dropping it undisposed holds nothing.
    /// </summary>
    private SemaphoreSlim WriterGateFor(string slug) => _writerGates.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    ///     A slug is validated to lowercase letters, digits and hyphens before it reaches here, so quoting
    ///     it as an identifier is safe. The quotes are for the hyphen, which is not an identifier character.
    /// </summary>
    private static string Quote(string slug) => $"\"{slug}\"";
}
