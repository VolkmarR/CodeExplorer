using CodeExplorer.Infrastructure;
using DuckDB.NET.Data;

namespace CodeExplorer.Index;

/// <summary>
///     How a reader gets a project: a lease on a pooled connection already bound with <c>USE</c>, and
///     the pool and the swap gate behind it. Apart from the restore and the shadow publish because it is
///     the path every read takes, and the question someone opening this file asks is how a read can be
///     held open across a swap.
/// </summary>
public sealed partial class ProjectIndexes
{
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
        await RestoreForReadAsync(slug, cancellationToken);

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

                return new IndexLease(connection, FtsAvailable, completed =>
                {
                    if (completed) pool.Return(connection, generation);
                    // Closed rather than pooled: nobody said the work on it completed, and the next
                    // borrower must not inherit whatever a statement that threw or was cancelled left.
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
        private const int _maxIdle = 4;

        // One lock over the generation and the idle stack, because the check and the add in Return
        // have to be one step against Discard: lock-free, a Discard landing between them bumped the
        // generation and emptied the pool, and the connection it had just condemned was added after it,
        // bound to a catalog about to be detached (#241). Held for a push or a pop, never for I/O.
        private readonly Lock _sync = new();
        private Stack<DuckDBConnection> _idle = new();
        private int _generation;

        /// <summary>
        ///     An idle connection and the generation it belongs to, read together so that what is
        ///     handed back is weighed against the state the pool was in when it was handed out.
        /// </summary>
        public DuckDBConnection? Rent(out int generation)
        {
            lock (_sync)
            {
                generation = _generation;
                return _idle.TryPop(out var connection) ? connection : null;
            }
        }

        public void Return(DuckDBConnection connection, int generation)
        {
            lock (_sync)
            {
                if (generation == _generation && _idle.Count < _maxIdle)
                {
                    _idle.Push(connection);
                    return;
                }
            }

            connection.Dispose();
        }

        /// <summary>
        ///     Closes every idle connection and marks the ones still out as not to be returned. Called
        ///     where the project's catalog is about to be detached, and on shutdown.
        /// </summary>
        public void Discard()
        {
            Stack<DuckDBConnection> idle;
            lock (_sync)
            {
                _generation++;
                idle = _idle;
                _idle = new Stack<DuckDBConnection>();
            }

            // Closed outside the lock: nothing can return one of these now, and a close is I/O.
            foreach (var connection in idle) connection.Dispose();
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
