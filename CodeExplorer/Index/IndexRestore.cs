using CodeExplorer.Reading;
using DuckDB.NET.Data;
using ModelContextProtocol;

namespace CodeExplorer.Index;

/// <summary>
///     Bringing a project's index back from its durable copy: for a read on a replica that woke without
///     it, and for a refresh, which restores without the BM25 index and settles it afterwards (#290).
///     Apart from the leases and the shadow publish because it is the one path that writes the live
///     file from somewhere other than a build.
/// </summary>
public sealed partial class ProjectIndexes
{
    /// <summary>
    ///     Puts the project's file on disk from its durable copy when the disk has none. The check
    ///     outside the writer gate is the fast path; <see cref="RestoreAsync" /> asks again inside it.
    /// </summary>
    public Task RestoreIfAbsentAsync(string slug, CancellationToken cancellationToken) =>
        HasIndex(slug) ? Task.CompletedTask : RestoreAsync(slug, FtsAvailable, static _ => { }, cancellationToken);

    /// <summary>
    ///     <see cref="RestoreIfAbsentAsync" /> for a read, which is where an agent's tool call enters the
    ///     index, and so the one place a restore's failure is put in the agent's terms (#291). Anything
    ///     other than an <c>McpException</c> reaches the agent as the SDK's generic error with no remedy.
    ///     A refusal already has its sentence and is passed on in it. A failure nobody wrote a sentence
    ///     for (the store unreachable, a Parquet file DuckDB could not read, a move the disk refused)
    ///     gets one here, and the original goes to the log, because its message names files on the
    ///     server. A refresh's restore is left alone: its status reports such a failure as the phase it
    ///     failed in (#262, #290).
    /// </summary>
    private async Task RestoreForReadAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            await RestoreIfAbsentAsync(slug, cancellationToken);
        }
        catch (ExplainedFailureException ex)
        {
            // Logged where it was thrown, with DuckDB's own message.
            throw new McpException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not McpException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Project {Project} could not be restored from its durable copy", slug);
            throw new McpException(
                $"The index of project '{slug}' could not be restored from its durable copy, so nothing was "
                + "restored. Try again shortly; if it keeps failing, ask the operator to refresh the project.", ex);
        }
    }

    /// <summary>
    ///     The restore a refresh begins with on a disk without the project's file (#229), reported
    ///     under its own phase and without the BM25 index (#290). The shadow the refresh builds next
    ///     replaces this index and builds its own full-text index, so building one here too paid the
    ///     most expensive phase of a large refresh twice. Until the swap the restored index is searched
    ///     by substring scan, which <c>index_info</c> says: every line is there, only ranked differently.
    ///     The restore is remembered as provisional until the swap replaces it, and a refresh that ends
    ///     without one hands it to <see cref="SettleRestoreAsync" />. <paramref name="report" /> is told
    ///     when the restore starts, which is what puts a failed restore under its own phase.
    /// </summary>
    public async Task RestoreForRefreshAsync(string slug, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        if (!HasIndex(slug) && await RestoreAsync(slug, false, report, cancellationToken) && FtsAvailable)
            _withoutFullText[slug] = 0;
    }

    /// <summary>
    ///     Gives an index <see cref="RestoreForRefreshAsync" /> restored without its BM25 index one of its
    ///     own, for a refresh that ended without the swap that would have replaced it — cancelled,
    ///     refused for disk, or failed. Left as it was, the project would be searched by substring scan
    ///     until a later refresh succeeded, which on a disk that is not wiped can be indefinitely. Nothing
    ///     to do after a swap, which is the ordinary case.
    ///     Built into a copy of the live file and moved into place like a restore, because a live index
    ///     is never mutated (CODING_STANDARDS.md); copied from the local file rather than fetched again,
    ///     because the transfer is most of what a restore costs and the store is the likeliest reason the
    ///     refresh failed. The cost lands only on a refresh that did.
    /// </summary>
    public async Task SettleRestoreAsync(string slug, CancellationToken cancellationToken)
    {
        if (!_withoutFullText.ContainsKey(slug)) return;

        using (await HoldWriterAsync(slug, cancellationToken))
        {
            // Asked again under the gate, where a swap or a delete clears it: either has left nothing
            // to settle.
            if (!_withoutFullText.TryRemove(slug, out _) || !HasIndex(slug)) return;

            await RebuildFileAsync(slug,
                "The index restored without a full-text index still serves; the next refresh builds one.",
                async connection =>
                {
                    await AttachAsync(connection, slug, FilePath(slug), cancellationToken);
                    await CopyWithFullTextAsync(connection, slug, cancellationToken);
                }, cancellationToken);
        }
    }

    /// <summary>
    ///     Copies every table of the attached live index into the empty one the connection is
    ///     <c>USE</c>ing, and builds the BM25 index over it. The table list is read from the empty
    ///     index's own schema, which is this build's, so a table added later is copied without a list
    ///     here to forget it in.
    /// </summary>
    private static async Task CopyWithFullTextAsync(DuckDBConnection connection, string slug,
        CancellationToken cancellationToken)
    {
        var tables = await connection.UnexplainedListAsync("SELECT table_name FROM duckdb_tables() "
                                                           + "WHERE database_name = current_database() AND schema_name = 'main'",
            [], reader => reader.GetString(0), cancellationToken);

        foreach (string table in tables)
            // index_info is the one row that changes: the copy is what gains the BM25 index.
            await connection.ExecuteAsync(table == "index_info"
                    ? $"INSERT INTO index_info SELECT schema_version, built_at, true, single_repository "
                      + $"FROM {Quote(slug)}.main.index_info"
                    : $"INSERT INTO {table} SELECT * FROM {Quote(slug)}.main.{table}",
                cancellationToken);

        await FtsExtension.CreateIndexAsync(connection, cancellationToken);
    }

    /// <summary>
    ///     Rebuilds a project's file from its durable copy, and answers whether this call put one in
    ///     place — not whether there is one now, which another caller may have restored. The
    ///     Parquet is loaded into a file of its own and that file is moved into place, so a restore
    ///     racing a first refresh cannot have the swap replace the file it is still writing — the move
    ///     goes through the same drain a swap does, and is the same one-file overwrite.
    ///     One project at a time and only that project: the gate is per project, so a wake that restores
    ///     a large index does not hold up a connection to a small one. <paramref name="fullText" /> is
    ///     whether the restored index gets a BM25 index of its own, and <paramref name="report" /> is
    ///     told when the restore starts.
    /// </summary>
    private async Task<bool> RestoreAsync(string slug, bool fullText, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        // Held from here to the end, not just around the file work: a swap that started while this was
        // downloading would otherwise install the newer index and have the move below overwrite it with
        // the older durable copy. Holding the writer gate across the whole restore is what makes the
        // recheck below decisive rather than a guess about what happens next.
        using (await HoldWriterAsync(slug, cancellationToken))
        {
            // Another caller may have restored it, or a refresh may have swapped one in, while this one
            // waited. Either way there is now an index and nothing to restore.
            if (HasIndex(slug)) return false;

            // Before the fetch and not once a copy is found: the transfer is most of what a restore
            // costs, and an unreachable store fails in it.
            report(new RefreshProgress(RefreshProgress.FetchStep, RefreshProgress.RestorePhase));

            // Null is a project that has never been indexed, or a copy an older schema wrote. Both
            // mean "rebuild from git", and both have recorded themselves on the way out.
            using var copy = await _durable.FetchAsync(slug, cancellationToken);
            if (copy is null) return false;

            await RebuildFileAsync(slug, "Nothing was restored; the next read of the project tries again.",
                connection => _durable.LoadAsync(connection, copy, fullText, cancellationToken), cancellationToken);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Restored project {Project} from its durable copy", slug);
            return true;
        }
    }

    /// <summary>
    ///     Builds a project's file beside the live one and puts it in place, for both restores: an empty
    ///     file under the restore catalog, filled by <paramref name="fill" /> on a connection of its own
    ///     that is <c>USE</c>ing it, then <see cref="PutInPlaceAsync" />. Run by a caller holding the
    ///     writer gate, which the two restores hold for more than this.
    /// </summary>
    /// <param name="slug">The project whose file is rebuilt.</param>
    /// <param name="remedy">The refusal's last sentence: what still serves, and what tries again.</param>
    /// <param name="fill">Writes every table into the empty file; the only step the restores differ in.</param>
    /// <param name="cancellationToken">Threaded through the attach, the fill and the move.</param>
    private async Task RebuildFileAsync(string slug, string remedy, Func<DuckDBConnection, Task> fill,
        CancellationToken cancellationToken)
    {
        string path = RestorePath(slug);
        string catalog = RestoreCatalog(slug);
        string refusal = NotPutInPlace(slug, "restored index", remedy);
        // The put-in-place closes it before the move; the using is for an attach, a fill or a checkpoint
        // that throws first.
        await using var connection = await ConnectAsync(cancellationToken);
        await AttachEmptyAsync(connection, catalog, path, cancellationToken);
        await fill(connection);
        await PutInPlaceAsync(connection, slug, catalog, path, refusal, "the restored index was put in place anyway",
            null, cancellationToken);
    }

    private string RestorePath(string slug) => Path.Combine(_directory, slug + ".restore.duckdb");

    /// <summary>The catalog a restore fills, kept apart from the live one the way a shadow's is.</summary>
    private static string RestoreCatalog(string slug) => slug + "$restore";
}
