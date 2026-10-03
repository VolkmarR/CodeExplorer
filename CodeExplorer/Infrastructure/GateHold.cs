using System.Collections.Concurrent;

namespace CodeExplorer.Infrastructure;

/// <summary>
///     Takes a gate as a scope rather than as a <c>WaitAsync</c> and a <c>Release</c> in a
///     <c>finally</c> the caller writes by hand.
/// </summary>
public static class GateHold
{
    /// <summary>Waits for the gate and holds it until the answer is disposed.</summary>
    public static async Task<Hold> HoldAsync(this SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        return new Hold(gate);
    }
}

/// <summary>
///     One taken gate, released when disposed and only the first time. A class and not a struct for
///     that: a struct copied into a <c>using</c> and disposed again through the original would release
///     twice, and a gate released by a holder it never let in admits a second writer.
/// </summary>
public sealed class Hold : IDisposable
{
    private SemaphoreSlim? _gate;

    internal Hold(SemaphoreSlim gate) => _gate = gate;

    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
}

/// <summary>
///     One gate per key, each letting one holder in at a time, so work on one project never waits on
///     work on another. A gate is created on first use and never removed, so a caller's keys must come
///     from a bounded set: project slugs are, bounded by the control database, and a gate holds nothing
///     but a count. Removal is left out on purpose, because dropping a gate that someone is waiting on
///     lets the next caller take a fresh one beside them. A semaphore that loses the <c>GetOrAdd</c>
///     race was never waited on, so dropping it undisposed holds nothing.
/// </summary>
public sealed class KeyedGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Waits for the key's gate and holds it until the answer is disposed.</summary>
    public Task<Hold> HoldAsync(string key, CancellationToken cancellationToken) =>
        _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1)).HoldAsync(cancellationToken);
}
