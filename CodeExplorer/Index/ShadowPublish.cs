using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Index;

/// <summary>
///     What a refresh's project must still be for <see cref="ProjectIndexes.PublishShadowAsync" /> to keep
///     the build (GHSA-253f-grfp-cqq7): discarded no more often than when the refresh began, and still
///     in the control database, which only the refresh can ask since <c>Index/</c> does not reach it.
/// </summary>
/// <param name="Discards">What <see cref="ProjectIndexes.DiscardCount" /> answered when the refresh began.</param>
/// <param name="ProjectExists">Asked under the writer gate, after the count.</param>
public sealed record PublishCondition(long Discards, Func<CancellationToken, Task<bool>> ProjectExists);

/// <summary>
///     The shadow a refresh builds, from its creation to the moment it replaces the live file or is
///     thrown away: the history it carries over, the conditions it is published under (GHSA-253f-grfp-cqq7)
///     and the checkpoint and move that put it in place. Apart from the leases and the restore because
///     it is the refresh's half of this class, and no read passes through it.
/// </summary>
public sealed partial class ProjectIndexes
{
    /// <summary>
    ///     Creates the shadow index a refresh fills: a second file beside the live one, attached under
    ///     its own catalog and returned with the tables created. The live index is untouched and keeps
    ///     answering every query until <see cref="PublishShadowAsync" /> (CONTEXT.md, ADR-0003).
    /// </summary>
    public async Task<ShadowIndex> CreateShadowAsync(string slug, CancellationToken cancellationToken)
    {
        // The carry-over below copies from the live file, so a wiped disk would otherwise re-walk all
        // of history the durable copy holds (#229). A refresh has restored by now; this keeps it a
        // property of every shadow rather than of one caller.
        await RestoreIfAbsentAsync(slug, cancellationToken);

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
    ///     what no longer belongs — a repository since removed from the project — alongside the rest of
    ///     the history it writes, so this copy stays a plain carry-over of everything.
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

        foreach (string table in _historyTables)
            await connection.ExecuteAsync(
                $"INSERT INTO {table} SELECT * FROM {Quote(slug)}.main.{table}", cancellationToken);
    }

    /// <summary>
    ///     How many times the project's index has been discarded on this replica. A refresh reads it
    ///     before it reads anything else about the project and hands it to
    ///     <see cref="PublishShadowAsync" /> in a <see cref="PublishCondition" />, which is how a build
    ///     that outlived a delete learns of it even when the slug has since been reused
    ///     (GHSA-253f-grfp-cqq7). In memory, because so is the refresh it guards: a restart ends both.
    /// </summary>
    public long DiscardCount(string slug) => _discards.GetValueOrDefault(slug);

    /// <summary>
    ///     Stores the finished shadow's durable copy and swaps it in, both under the project's writer
    ///     gate, unless <paramref name="condition" /> says the project is gone. False is that case:
    ///     nothing was stored or swapped, and the shadow is the caller's to discard.
    ///     The check and the two writes are one hold of the gate because a delete is the other writer:
    ///     checked outside it, a delete landing in between would have its index put back.
    ///     The swap waits for in-flight queries with a hard timeout; callers arriving mid-swap wait
    ///     rather than see a project with no index, so a search answers completely from the old index
    ///     or completely from the new one. The shadow is disposed between the store, which exports from
    ///     its connection, and the swap, which cannot move the file while that connection holds it.
    /// </summary>
    /// <param name="shadow">The filled shadow index. Disposed by this call once it has been stored.</param>
    /// <param name="condition">What the project must still be for the build to be kept.</param>
    /// <param name="report">Told when the swap starts, which is the one phase this call begins.</param>
    /// <param name="cancellationToken">Threaded through the gate, the store and the swap.</param>
    public async Task<bool> PublishShadowAsync(ShadowIndex shadow, PublishCondition condition,
        Action<RefreshProgress> report, CancellationToken cancellationToken)
    {
        string slug = shadow.Slug;
        using (await HoldWriterAsync(slug, cancellationToken))
        {
            if (!await StillHoldsAsync(slug, condition, cancellationToken)) return false;

            // Exported from the shadow rather than from the live index after the swap, which is what
            // the tables about to be swapped in are. Doing it here means the export needs no second
            // attach of the live catalog — one that would quietly re-bind a connection ADR-0003 says the
            // swap must strand — and a store that is unreachable leaves the old index serving.
            await _durable.StoreAsync(shadow.Connection, slug, cancellationToken);
            string catalog = ShadowCatalog(slug);
            string refusal = NotPutInPlace(slug, "new index",
                "The index that was serving still is; refresh the project to try again.");
            await CheckpointAsync(shadow.Connection, slug, catalog, refusal, cancellationToken);
            shadow.Dispose();

            report(new RefreshProgress(RefreshProgress.SwapStep, RefreshProgress.TotalStepCount,
                RefreshProgress.SwapPhase));
            await ReplaceFileAsync(slug, "the new index was swapped in anyway",
                MoveIntoPlace(slug, catalog, ShadowPath(slug), refusal, cancellationToken), cancellationToken);
            // The shadow built its own full-text index, so a restore it replaced has nothing to settle.
            _withoutFullText.TryRemove(slug, out _);
            return true;
        }
    }

    /// <summary>
    ///     Whether the project is still the one a refresh began on. Both questions, because each misses a
    ///     delete the other sees: the count misses one whose discard never got the writer gate, and the
    ///     control database misses a delete followed by a create under the same slug.
    /// </summary>
    public async Task<bool> StillHoldsAsync(string slug, PublishCondition condition,
        CancellationToken cancellationToken) =>
        DiscardCount(slug) == condition.Discards && await condition.ProjectExists(cancellationToken);

    /// <summary>
    ///     The file work of a swap and of a restore: a finished file, attached under its own catalog, made
    ///     the project's live one. Run with the gate shut, by a caller holding the writer gate, after
    ///     <see cref="CheckpointAsync" /> has written the file out.
    /// </summary>
    /// <param name="slug">The project whose live file is replaced.</param>
    /// <param name="catalog">The catalog the finished file is attached under.</param>
    /// <param name="path">The finished file.</param>
    /// <param name="refusal">Thrown when a log is left beside the file; see <see cref="NotPutInPlace" />.</param>
    /// <param name="cancellationToken">Threaded through both detaches.</param>
    private Func<DuckDBConnection, Task> MoveIntoPlace(string slug, string catalog, string path, string refusal,
        CancellationToken cancellationToken) =>
        async connection =>
        {
            // Both catalogs go before the move: DETACH is what closes the file handles, and neither file
            // can be deleted or moved while the instance holds one. The new one first, so a refusal
            // below leaves the live catalog attached and serving. Its DETACH checkpoints again, and
            // reports a checkpoint that failed as its own error after detaching anyway.
            await WrittenOutOrRefusedAsync(() => DetachAsync(connection, catalog, cancellationToken), slug, refusal,
                cancellationToken);
            // A log still here holds rows the file does not — written after the checkpoint, and not
            // checkpointed by a DETACH that found the database still in use — and moving the file
            // without it would put an index missing its tail in place. Refused rather than moved with
            // it: nothing opens a log under a name other than the one it was written beside.
            if (File.Exists(path + ".wal")) throw new ExplainedFailureException(refusal);
            await DetachAsync(connection, slug, cancellationToken);
            // One overwriting move, never delete-then-move: a move that fails after the old file was
            // deleted would leave the project with no index at all, and the caller's cleanup would then
            // take the new file too. Overwrite replaces the file or leaves it exactly as it was.
            File.Move(path, FilePath(slug), true);
            // The old live file's log, left by a DETACH that did not checkpoint it: it belongs to the
            // file just replaced, and would replay that file's tail over the new one.
            File.Delete(FilePath(slug) + ".wal");
        };

    /// <summary>
    ///     Writes everything committed to a finished file into the file itself, or throws
    ///     <paramref name="refusal" />. Explicit rather than trusted to the DETACH: a DETACH checkpoints
    ///     only when nothing else is using the database, and one that did not leaves committed rows of the
    ///     new file — the <c>index_info</c> row a build writes last among them — in a log beside it (#242).
    ///     Run before the drain and not inside the file work, because nothing reads a finished file yet
    ///     and the flush would otherwise hold every reader of the project and every attach out.
    /// </summary>
    private Task CheckpointAsync(DuckDBConnection connection, string slug, string catalog, string refusal,
        CancellationToken cancellationToken) =>
        WrittenOutOrRefusedAsync(() => connection.ExecuteAsync($"CHECKPOINT {Quote(catalog)}", cancellationToken),
            slug, refusal, cancellationToken);

    /// <summary>
    ///     Runs a statement that checkpoints a finished file — the explicit <c>CHECKPOINT</c>, and the
    ///     <c>DETACH</c> that checkpoints again on its way out — and turns its failure into
    ///     <paramref name="refusal" />. Refused whether or not a log is left: DuckDB invalidates the
    ///     database a checkpoint failed on, so the file is not trusted. DuckDB's message names the file,
    ///     so it goes to the log and the operator's sentence to the status.
    /// </summary>
    private async Task WrittenOutOrRefusedAsync(Func<Task> statement, string slug, string refusal,
        CancellationToken cancellationToken)
    {
        try
        {
            await statement();
        }
        catch (DuckDBException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The finished index of project {Project} could not be written to its file", slug);
            throw new ExplainedFailureException(refusal, ex);
        }
    }

    /// <summary>Why a finished file was not made the project's live one, in the operator's words.</summary>
    /// <param name="slug">The project.</param>
    /// <param name="replacement">What the file is: "new index", "restored index".</param>
    /// <param name="remedy">What still serves, and what tries again.</param>
    private static string NotPutInPlace(string slug, string replacement, string remedy) =>
        $"The {replacement} of project '{slug}' was not put in place, because part of it had not been "
        + $"written to its file yet. {remedy}";

    /// <summary>
    ///     Removes the shadow file after a refresh failed part-way. The live index is untouched and
    ///     still serving, which is the whole point of building beside it rather than in place.
    /// </summary>
    public async Task DiscardShadowAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(cancellationToken);
        // No drain: nothing reads a shadow, so there is nobody to wait for. This is the one attach-gate
        // caller that is not replacing what the readers are using.
        await UnderAttachGateAsync(async () =>
        {
            await DetachAsync(connection, ShadowCatalog(slug), cancellationToken);
            DeleteIndexFile(ShadowPath(slug));
        }, cancellationToken);
    }

    private string ShadowPath(string slug) => Path.Combine(_directory, slug + ".shadow.duckdb");

    /// <summary>
    ///     The catalog the shadow of a project is attached under. A slug is lowercase letters, digits
    ///     and hyphens, so no project can be called this and a shadow can never collide with a live index.
    /// </summary>
    private static string ShadowCatalog(string slug) => slug + "$shadow";
}
