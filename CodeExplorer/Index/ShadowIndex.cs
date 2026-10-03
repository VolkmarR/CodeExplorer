using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Index;

/// <summary>
///     The shadow index a refresh fills (CONTEXT.md): a connection already <c>USE</c>ing the shadow
///     file, and the catalog name an appender targets. It is a second file next to the live one, so
///     the live index keeps answering queries until <see cref="ProjectIndexes.PublishShadowAsync" />.
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
            report(new RefreshProgress(RefreshProgress.FullTextStep, RefreshProgress.FullTextPhase));
            await FtsExtension.CreateIndexAsync(Connection, cancellationToken);
        }

        await using var command = Connection.Query("INSERT INTO index_info VALUES ($version, now(), $fts, $single)",
        [
            new("version", ProjectIndexes.SchemaVersion), new("fts", fullTextLoaded),
            new("single", singleRepository)
        ]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose() => Connection.Dispose();
}
