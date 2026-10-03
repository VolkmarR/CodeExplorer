using CodeExplorer.Infrastructure;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The one disposable hold every gate in the server is taken through (<see cref="GateHold" />), and
///     the per-key gate built on it (<see cref="KeyedGate" />). What a caller relies on is that a scope
///     releases exactly what it took: once, and nothing when the wait never ended.
/// </summary>
public sealed class GateHoldTests
{
    [Fact]
    public async Task Disposing_a_hold_releases_the_gate()
    {
        using var gate = new SemaphoreSlim(1, 1);

        var hold = await gate.HoldAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, gate.CurrentCount);

        hold.Dispose();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Disposing_a_hold_twice_releases_once()
    {
        // Two slots, so a second release would not throw SemaphoreFullException and hide the miscount.
        using var gate = new SemaphoreSlim(1, 2);

        var hold = await gate.HoldAsync(TestContext.Current.CancellationToken);
        hold.Dispose();
        hold.Dispose();

        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task A_wait_cancelled_before_the_gate_opens_throws_and_holds_nothing()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var first = await gate.HoldAsync(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var waiting = gate.HoldAsync(cancel.Token);
        Assert.False(waiting.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // The first holder's release is the only one: the gate is free again, and still has one slot.
        first.Dispose();
        Assert.Equal(1, gate.CurrentCount);
        Assert.True(gate.Wait(0, TestContext.Current.CancellationToken));
        Assert.False(gate.Wait(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Two_keys_do_not_wait_for_each_other_and_one_key_does()
    {
        var gates = new KeyedGate();
        using var alpha = await gates.HoldAsync("alpha", TestContext.Current.CancellationToken);

        var beta = gates.HoldAsync("beta", TestContext.Current.CancellationToken);
        var alphaAgain = gates.HoldAsync("alpha", TestContext.Current.CancellationToken);

        Assert.True(beta.IsCompletedSuccessfully);
        Assert.False(alphaAgain.IsCompleted);

        alpha.Dispose();
        (await alphaAgain).Dispose();
        (await beta).Dispose();
    }
}
