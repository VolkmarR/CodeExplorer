using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     A project's durable copy, fetched and waiting to be loaded: the Parquet files in a scratch
///     folder of their own, and the recording of the fetch that is still running. It exists as a type
///     because the fetch, the schema check and the load are three steps a caller has to put a file
///     creation between — <see cref="ProjectIndexes" /> cannot create the database to load into until
///     the copy is known to be there and to fit.
///     Disposing it removes the folder and closes the recording, so a fetch that is abandoned between
///     the two is still measured.
/// </summary>
public sealed class DurableCopy(string directory, Telemetry.DurableCopyRecording recording) : IDisposable
{
    internal string Directory { get; } = directory;

    internal Telemetry.DurableCopyRecording Recording { get; } = recording;

    public void Dispose()
    {
        Recording.Dispose();
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}

/// <summary>
///     The durable copy of a project index (CONTEXT.md): <c>COPY TO</c> writes the tables to local
///     Parquet, the store moves them out, and a restore reads them back into a fresh database file.
///     This is what makes scale to zero survivable — the container's disk is wiped on every stop, and
///     the Parquet set is the only copy of an index that outlives a replica.
///     It owns the Parquet and the store and nothing else: which file a table is loaded into, and
///     when, is <see cref="ProjectIndexes" />'s, which is the only thing that opens a project file.
/// </summary>
public sealed class DurableIndex(IConfiguration configuration, DurableStore store, ILogger<DurableIndex> logger)
{
    /// <summary>
    ///     The tables of a project index, in the order a restore may insert them. There is no foreign
    ///     key between them, so the order is for readability rather than for the engine.
    /// </summary>
    private static readonly string[] Tables =
    [
        "index_info", "repositories", "files", "lines", "commits", "commit_files", "attribution",
        // The rename chains the build derived (#148). Derived and still carried: a restore does not
        // re-walk, so an index that lost this would stop telling a caller what a scope was called
        // before — silently, because a missing chain is indistinguishable from a path nobody renamed.
        "path_lineage",
        // The import edges the build read and resolved (#55). They travel with the tables, like the
        // overview and for the same reason: a restored index that had lost them would answer
        // `who_imports` with nothing, which reads as "nothing depends on this file".
        "imports",
        // The overview the build computed (#51). It travels with the tables it was derived from, so a
        // restored index answers project_overview without a rebuild — which is the whole point of
        // computing it at build time rather than per call.
        "project_overview"
    ];

    /// <summary>
    ///     Where <c>COPY TO</c> writes and a fetch lands: on the volume ADR-0003 budgets, next to the
    ///     indexes, because a Parquet set is the size of the index that produced it and the ephemeral
    ///     disk is the one ceiling this app cannot raise.
    /// </summary>
    private readonly string _scratch =
        Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "scratch");

    /// <summary>
    ///     Writes a project's durable copy: every table to a local Parquet file, then each file to the
    ///     store under the project's own prefix. The connection is already bound to the project with
    ///     <c>USE</c>, so <c>COPY</c> resolves the tables in it and this never picks a catalog of its own.
    /// </summary>
    public async Task StoreAsync(DuckDBConnection connection, string slug, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.DurableCopy(slug, Telemetry.StoreOperation);
        string scratch = Scratch(slug);
        try
        {
            foreach (string table in Tables)
            {
                string local = Path.Combine(scratch, table + ".parquet");
                // ZSTD over the default SNAPPY: the transfer and the blob bill are what this is paying
                // for, and line content compresses far enough that the extra CPU is repaid on the way
                // back too.
                await connection.ExecuteAsync(
                    $"COPY (SELECT * FROM {table}) TO '{Escape(local)}' (FORMAT parquet, COMPRESSION zstd)",
                    cancellationToken);
                await store.StoreAsync(Name(slug, table), local, cancellationToken);
            }

            recording.Moved();
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    /// <summary>
    ///     Fetches a project's durable copy, or answers null when the store holds none and when what it
    ///     holds an older build wrote. A copy from an older schema is not restored but rebuilt from git:
    ///     its columns no longer fit the tables this build creates, and loading it would be a silently
    ///     wrong index rather than a missing one.
    ///     The caller disposes the result, which removes the scratch folder and closes the recording;
    ///     an answer of null has recorded itself already.
    /// </summary>
    public async Task<DurableCopy?> FetchAsync(string slug, CancellationToken cancellationToken)
    {
        // Started here and not in LoadAsync, so the measurement covers the transfer — which on a cold
        // wake is most of what a restore costs — and not only the insert that follows it.
        var copy = new DurableCopy(Scratch(slug), Telemetry.DurableCopy(slug, Telemetry.FetchOperation));
        try
        {
            foreach (string table in Tables)
                if (!await store.FetchAsync(Name(slug, table), Path.Combine(copy.Directory, table + ".parquet"),
                        cancellationToken))
                    // A partial set is as good as none: every table is written by one store, so a
                    // missing one is a store that never finished and a half-loaded index would be worse.
                    return Absent(copy);

            int version = await SchemaVersionAsync(copy, cancellationToken);
            if (version == ProjectIndexes.SchemaVersion) return copy;

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "The durable copy of project {Project} was written for schema version {Found}, and this "
                    + "build reads version {Expected}; its index is rebuilt from git instead of restored",
                    slug, version, ProjectIndexes.SchemaVersion);
            return Absent(copy);
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Loads a fetched copy into a database whose tables the caller has already created and which
    ///     its connection is already <c>USE</c>ing. The BM25 index is rebuilt here rather than carried in
    ///     the Parquet: it is <c>fts</c>'s own tables, and a replica that could not install the extension
    ///     has to answer from a substring scan, which is what <paramref name="ftsAvailable" /> decides
    ///     and what <c>index_info</c> then reports.
    /// </summary>
    public async Task LoadAsync(DuckDBConnection connection, DurableCopy copy, bool ftsAvailable,
        CancellationToken cancellationToken)
    {
        foreach (string table in Tables)
        {
            // index_info is the one table not copied straight back: whether a BM25 index exists is a
            // property of this replica and of the load below, not of the build that wrote the Parquet.
            string columns = table == "index_info"
                ? $"schema_version, built_at, {(ftsAvailable ? "true" : "false")} AS fts_indexed, single_repository"
                : "*";
            await connection.ExecuteAsync(
                $"INSERT INTO {table} SELECT {columns} FROM read_parquet('{Escape(Parquet(copy, table))}')",
                cancellationToken);
        }

        if (ftsAvailable) await FtsExtension.CreateIndexAsync(connection, cancellationToken);

        copy.Recording.Moved();
    }

    /// <summary>Forgets a project's durable copy, for a project an operator deleted.</summary>
    public Task RemoveAsync(string slug, CancellationToken cancellationToken) =>
        store.RemoveAsync($"indexes/{slug}/", cancellationToken);

    /// <summary>
    ///     There was nothing to load: no copy, or one an older build wrote. Recorded as such rather than
    ///     as a move, so a dashboard can tell a wake that restored from one that rebuilt.
    /// </summary>
    private static DurableCopy? Absent(DurableCopy copy)
    {
        copy.Recording.Absent();
        copy.Dispose();
        return null;
    }

    /// <summary>
    ///     The schema version the copy was written for, or -1 when the file does not say — which a
    ///     Parquet set from before the column existed does not, and which must read as "do not restore".
    /// </summary>
    private static async Task<int> SchemaVersionAsync(DurableCopy copy, CancellationToken cancellationToken)
    {
        // The one memory database in this codebase, and CODING_STANDARDS forbids it in tests rather
        // than here: nothing is stored in it, it only reads a file the caller already has on disk, and
        // giving it a file of its own would be a file to clean up for a single scalar.
        using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // read_parquet reads the footer for the columns and only the one row group for the value, so
        // this costs a stat and a few kilobytes rather than the whole set.
        command.CommandText =
            $"SELECT schema_version FROM read_parquet('{Escape(Parquet(copy, "index_info"))}') LIMIT 1";
        try
        {
            return await command.ExecuteScalarAsync(cancellationToken) is int version ? version : -1;
        }
        catch (DuckDBException)
        {
            // Safe to swallow: an unreadable or differently shaped index_info is exactly the case this
            // check exists for, and the answer to both is to rebuild from git.
            return -1;
        }
    }

    private static string Parquet(DurableCopy copy, string table) =>
        Path.Combine(copy.Directory, table + ".parquet");

    /// <summary>A scratch folder nothing else is using, so two projects never write over each other.</summary>
    private string Scratch(string slug)
    {
        string directory = Path.Combine(_scratch, $"{slug}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Every project under its own prefix, which is what makes one project's copy removable on its own.</summary>
    private static string Name(string slug, string table) => $"indexes/{slug}/{table}.parquet";

    /// <summary>
    ///     A path as a SQL string literal. Paths are built from configuration, the slug and a table name
    ///     from the list above, never from a request.
    /// </summary>
    private static string Escape(string path) => path.Replace("'", "''");
}
