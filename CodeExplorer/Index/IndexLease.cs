using DuckDB.NET.Data;

namespace CodeExplorer.Index;

/// <summary>
///     A connection bound to one project for one unit of work, and the record that the work is in
///     flight so a swap waits for it. Dispose it as soon as that unit of work is done and never keep
///     it: a swap detaches the catalog underneath, and the next statement on a connection that held
///     <c>USE</c> across it fails with <c>Catalog does not exist</c> (ADR-0003).
/// </summary>
public sealed class IndexLease(DuckDBConnection connection, bool fullTextLoaded, Action<bool> release)
    : IDisposable
{
    private bool _completed;
    private int _released;

    public DuckDBConnection Connection { get; } = connection;

    /// <summary>
    ///     Whether this process can run <c>match_bm25</c> at all. One of the two truths behind a
    ///     full-text search; the other, whether this file holds a BM25 index, is in its
    ///     <c>index_info</c>, and <c>IndexReader.HasFullTextAsync</c> is where they meet.
    /// </summary>
    public bool FullTextLoaded { get; } = fullTextLoaded;

    /// <summary>
    ///     Says the work on this connection finished cleanly, so it may be handed to someone else. A
    ///     lease disposed without it is closed rather than pooled: a statement that threw or was
    ///     cancelled can leave a result part-read behind it, and the next borrower would inherit it.
    ///     Pooling was once the default and closing the exception a caller had to ask for, and a status
    ///     read that forgot to ask pooled a cancelled connection (#241); inverted, a forgotten call
    ///     costs a new connection, never a reused bad one (#267). <see cref="CodeExplorer.Reading.IndexReaders" /> says it
    ///     for every lease it opens, in one helper.
    /// </summary>
    public void Completed() => _completed = true;

    public void Dispose()
    {
        // The connection is not closed here: if the work said it completed it goes back to its
        // project's pool, and releasing is what puts it there and only then lets a swap through
        // (#149). The order is the point — a connection handed back after the drain had counted this
        // reader out would land in a pool the swap had already emptied, and the next caller would get
        // a binding to a detached catalog.
        // Guarded, because a double dispose would let a swap through while another lease still holds
        // the project — the one thing the drain exists to prevent.
        if (Interlocked.Exchange(ref _released, 1) == 0) release(_completed);
    }
}
