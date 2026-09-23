using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Index;

/// <summary>
///     Where DuckDB keeps the <c>fts</c> extension, and how it gets there before a replica needs it.
///     <c>INSTALL fts</c> downloads on first use, and fails silently when it cannot: the whole replica
///     then answers by substring scan, which ranks differently and is not an error anyone sees. The
///     image build installs the extension into a directory the runtime stage carries, and the runtime
///     points DuckDB at it, so production is always BM25 (#14, ADR-0004).
/// </summary>
public static class FtsExtension
{
    /// <summary>
    ///     The first argument that makes the process install the extension and exit instead of serving.
    ///     A switch rather than a second project, because the install has to run against the same DuckDB
    ///     build the server loads: an extension is version- and platform-stamped, and one fetched by any
    ///     other means would be ignored by the binary that has to load it.
    /// </summary>
    public const string InstallArgument = "--install-fts";

    /// <summary>
    ///     Installs <c>fts</c> into the given directory, reaching the network to do so. Run in the image
    ///     build, where the network is available and a failure should stop the build — so nothing is
    ///     swallowed here, unlike the runtime load, where <see cref="SearchEngine.Auto" /> has substring
    ///     scan to fall back to.
    /// </summary>
    /// <param name="directory">Where the extension is written; created when it does not exist.</param>
    public static void InstallTo(string directory)
    {
        // In-memory because nothing is stored: INSTALL writes to the extension directory, not to the
        // database, and the build stage has no data directory to put a file in.
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        UseDirectory(connection, directory);

        using var command = connection.CreateCommand();
        // LOAD as well as INSTALL, so a download that lands unusable fails the build rather than the
        // first search on a replica with no egress.
        command.CommandText = "INSTALL fts; LOAD fts";
        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     Loads the extension into the instance behind the connection, installing it first when the
    ///     directory has no copy. True when full-text search is available for the life of the process.
    ///     False only under <see cref="SearchEngine.Auto" />, whose documented offline fallback is the
    ///     substring scan (ADR-0004); asked for outright, an extension that cannot be loaded is a
    ///     configuration error and throws.
    /// </summary>
    /// <param name="connection">An open connection to the instance; the load is instance-wide like the directory.</param>
    /// <param name="engine">What the deployment asked for. <see cref="SearchEngine.Substring" /> never loads.</param>
    /// <param name="logger">Where the downgrade is reported, which is how an operator learns of it.</param>
    internal static bool TryLoad(DuckDBConnection connection, SearchEngine engine, ILogger logger)
    {
        if (engine == SearchEngine.Substring) return false;
        try
        {
            // INSTALL downloads the extension on first use; #14 bakes it into the image so production
            // never reaches out. Offline, it throws here rather than failing silently later.
            using var command = connection.CreateCommand();
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

    /// <summary>
    ///     Builds the BM25 index over <c>lines</c> in the database the connection is <c>USE</c>ing. One
    ///     place for the build and the restore, because a restored index tokenised differently from the
    ///     one it is a copy of would rank differently. Tokens are lower-cased identifiers: letters,
    ///     digits and underscore. No stemming and no stop words, because <c>Get</c>, <c>if</c> and
    ///     <c>id</c> are exactly what an agent searches code for. Runs inside the attached database
    ///     because <c>match_bm25</c> only resolves its tables in the current one.
    /// </summary>
    internal static async Task CreateIndexAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              PRAGMA create_fts_index('lines', 'line_id', 'content',
                                  stemmer = 'none', stopwords = 'none', ignore = '[^a-z0-9_]+',
                                  lower = 1, strip_accents = 0, overwrite = 1)
                              """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Points the connection's DuckDB instance at the directory, or leaves DuckDB's own default —
    ///     a folder under the user profile — when none is configured. The setting is instance-wide like
    ///     <c>memory_limit</c> (ADR-0003), so setting it on the anchor covers every connection that
    ///     shares its connection string; it must be set before <c>INSTALL</c> looks for a local copy.
    /// </summary>
    /// <param name="connection">An open connection to the instance the setting applies to.</param>
    /// <param name="directory">Configured as <c>Index:ExtensionDirectory</c>; absent is an answer.</param>
    internal static void UseDirectory(DuckDBConnection connection, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        // DuckDB creates the version and platform folders beneath it, but not the root: an absent
        // directory makes INSTALL fail with an IO error naming a path nobody configured.
        Directory.CreateDirectory(directory);
        using var command = connection.CreateCommand();
        // Inlined rather than parameterised: SET takes no parameters in DuckDB, and the value comes
        // from this deployment's own configuration rather than from a request.
        command.CommandText = $"SET extension_directory = {IndexQuery.Literal(directory)}";
        command.ExecuteNonQuery();
    }
}
