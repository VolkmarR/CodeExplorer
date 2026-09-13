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
///     The one DuckDB instance every project index is attached to (ADR-0003). Owns the attach state,
///     hands out connections already bound with <c>USE</c>, and creates the empty file a build fills.
///     Nothing else opens a project file.
/// </summary>
public sealed class ProjectIndexes : IDisposable
{
    /// <summary>
    ///     Bumped when the tables below change shape, so a Parquet export from an older build is rebuilt
    ///     from git instead of restored into a schema it no longer fits (#9).
    /// </summary>
    public const int SchemaVersion = 1;

    private readonly string _directory;
    private readonly string _connectionString;

    // Attached databases and loaded extensions belong to the instance, and DuckDB.NET disposes the
    // instance once its last connection closes. This connection is never used for queries; it only
    // keeps the instance, and with it every ATTACH and the LOAD below, alive for the process.
    private readonly DuckDBConnection _anchor;

    // ATTACH is instance-wide, so two connections attaching the same slug at once race on the same
    // file. The gate serialises attach and detach; the set remembers what the instance already holds.
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private readonly HashSet<string> _attached = [];

    public ProjectIndexes(IConfiguration configuration, ILogger<ProjectIndexes> logger)
    {
        _directory = Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "indexes");
        Directory.CreateDirectory(_directory);
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

    public string FilePath(string slug) => Path.Combine(_directory, slug + ".duckdb");

    public bool HasIndex(string slug) => File.Exists(FilePath(slug));

    /// <summary>
    ///     A connection bound to the project with <c>USE</c>, attached first if the instance does not hold
    ///     it yet. Callers dispose it after one unit of work and never keep it: a refresh detaches under it
    ///     and the next statement fails with <c>Catalog does not exist</c> (ADR-0003). Null when the project
    ///     has no index yet, which is an answer for the caller to phrase, not a failure.
    /// </summary>
    public async Task<DuckDBConnection?> OpenAsync(string slug, CancellationToken cancellationToken)
    {
        if (!HasIndex(slug)) return null;

        var connection = await ConnectAsync(cancellationToken);
        try
        {
            await AttachAsync(connection, slug, cancellationToken);
            await ExecuteAsync(connection, $"USE {Quote(slug)}", cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Detaches and deletes whatever the project had, attaches a fresh empty file and returns a
    ///     connection bound to it with the tables created. Until #8 builds a shadow file and swaps, a
    ///     rebuild therefore takes the project offline for the duration; connections opened earlier fail
    ///     on their next statement, as ADR-0003 describes.
    /// </summary>
    public async Task<DuckDBConnection> CreateAsync(string slug, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(cancellationToken);
        try
        {
            await _attachGate.WaitAsync(cancellationToken);
            try
            {
                await ExecuteAsync(connection, $"DETACH DATABASE IF EXISTS {Quote(slug)}", cancellationToken);
                _attached.Remove(slug);
                File.Delete(FilePath(slug));
                File.Delete(FilePath(slug) + ".wal");
                await ExecuteAsync(connection, $"ATTACH {Literal(FilePath(slug))} AS {Quote(slug)}", cancellationToken);
                _attached.Add(slug);
            }
            finally
            {
                _attachGate.Release();
            }

            await ExecuteAsync(connection, $"USE {Quote(slug)}", cancellationToken);
            await ExecuteAsync(connection, Schema, cancellationToken);
            return connection;
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
    public async Task<bool> CompleteBuildAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        if (FtsAvailable)
        {
            // Tokens are lower-cased identifiers: letters, digits and underscore. No stemming and no stop
            // words, because `Get`, `if` and `id` are exactly what an agent searches code for. Runs inside
            // the attached database because match_bm25 only resolves its tables in the current one.
            await ExecuteAsync(connection, """
                PRAGMA create_fts_index('lines', 'line_id', 'content',
                    stemmer = 'none', stopwords = 'none', ignore = '[^a-z0-9_]+',
                    lower = 1, strip_accents = 0, overwrite = 1)
                """, cancellationToken);
        }

        // Written last, so a row in index_info means the build completed and describes what exists.
        await ExecuteAsync(connection,
            $"INSERT INTO index_info VALUES ({SchemaVersion}, now(), {(FtsAvailable ? "true" : "false")})",
            cancellationToken);
        return FtsAvailable;
    }

    /// <summary>
    ///     Removes a project's file after a failed build, so <see cref="HasIndex" /> does not report a
    ///     half-written index as one that can be served.
    /// </summary>
    public async Task DiscardAsync(string slug, CancellationToken cancellationToken)
    {
        using var connection = await ConnectAsync(cancellationToken);
        await _attachGate.WaitAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, $"DETACH DATABASE IF EXISTS {Quote(slug)}", cancellationToken);
            _attached.Remove(slug);
            File.Delete(FilePath(slug));
            File.Delete(FilePath(slug) + ".wal");
        }
        finally
        {
            _attachGate.Release();
        }
    }

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
            schema_version INTEGER NOT NULL,
            built_at       TIMESTAMPTZ NOT NULL,
            fts_indexed    BOOLEAN NOT NULL);
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

    private async Task AttachAsync(DuckDBConnection connection, string slug, CancellationToken cancellationToken)
    {
        await _attachGate.WaitAsync(cancellationToken);
        try
        {
            if (_attached.Contains(slug)) return;
            await ExecuteAsync(connection, $"ATTACH IF NOT EXISTS {Literal(FilePath(slug))} AS {Quote(slug)}",
                cancellationToken);
            _attached.Add(slug);
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

    /// <summary>
    ///     A slug is validated to lowercase letters, digits and hyphens before it reaches here, so quoting
    ///     it as an identifier is safe. The quotes are for the hyphen, which is not an identifier character.
    /// </summary>
    private static string Quote(string slug) => $"\"{slug}\"";

    /// <summary>A file path as a SQL string literal. Paths come from configuration and the slug, never from a request.</summary>
    private static string Literal(string path) => $"'{path.Replace("'", "''")}'";

    public void Dispose()
    {
        _anchor.Dispose();
        _attachGate.Dispose();
    }
}
