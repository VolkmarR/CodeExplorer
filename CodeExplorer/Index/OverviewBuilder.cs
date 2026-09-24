using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Index;

/// <summary>
///     Computes a project's overview and stores it with the index that produced it (<c>#51</c>), for
///     <c>project_overview</c> to answer from. It runs inside the build, after the files and the
///     history are in the shadow, because every section is an aggregate over those tables. Stored
///     rather than computed per call because it is the first thing an agent asks, so a caller
///     orienting itself pays a single row read rather than five aggregates over the largest tables in
///     the index.
///     The sections are <see cref="OverviewQueries" />', over <see cref="OverviewScope.Stored" />. The
///     overview page runs the same statements live over a scope of its own (#216) and never reads this
///     row, so the two differ only by what the page was asked to filter or leave out.
/// </summary>
public sealed class OverviewBuilder
{
    /// <summary>
    ///     Fills the overview row of a shadow index. The build reports this as
    ///     <see cref="RefreshProgress.OverviewStep" /> before calling it; nothing in here reports
    ///     further, because every statement it runs is one aggregate over tables already on this
    ///     connection and has no count worth polling.
    /// </summary>
    /// <param name="shadow">The shadow being built, its connection already bound to it.</param>
    /// <param name="singleRepository">How this project names its files (ADR-0006), for the paths in the row.</param>
    /// <param name="cancellationToken">Threaded through every statement.</param>
    public async Task<IndexOverview> FillAsync(ShadowIndex shadow, bool singleRepository,
        CancellationToken cancellationToken)
    {
        var connection = shadow.Connection;
        // The naming rule is ProjectPaths', including which repository anchors it: the build writes
        // qualified paths into the stored row and every read parses them back, so an anchor chosen here
        // that disagreed with the reader's would bake the disagreement into the index until the next
        // full rebuild rather than fail a read.
        var paths = ProjectPaths.For(singleRepository, await SlugsAsync(connection, cancellationToken),
            shadow.Slug);

        var (overview, _) = await OverviewQueries.ComputeAsync(connection, paths, OverviewScope.Stored,
            cancellationToken);

        await using var insert = connection.Query("INSERT INTO project_overview VALUES ($document)",
            [new DuckDBParameter("document", overview.ToDocument())]);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return overview;
    }

    /// <summary>
    ///     The repositories this build read, in the order it numbered them, which is the order
    ///     <see cref="ProjectPaths.For(bool,IEnumerable{string},string)" /> takes the anchor from.
    /// </summary>
    private static async Task<List<string>> SlugsAsync(DuckDBConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.Query("SELECT slug FROM repositories ORDER BY repo_id", []);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var slugs = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) slugs.Add(reader.Text("slug"));
        return slugs;
    }
}
