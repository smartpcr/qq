using AgentSwarm.Messaging.Teams;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class InMemoryActivityIdStoreTests
{
    [Fact]
    public async Task IsSeenOrMarkAsync_ReturnsFalse_OnFirstObservation()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);

        var seen = await store.IsSeenOrMarkAsync("activity-1", default);

        Assert.False(seen);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task IsSeenOrMarkAsync_ReturnsTrue_OnSubsequentObservation()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);

        await store.IsSeenOrMarkAsync("activity-1", default);
        var second = await store.IsSeenOrMarkAsync("activity-1", default);

        Assert.True(second);
    }

    [Fact]
    public async Task IsSeenOrMarkAsync_RejectsEmptyIds()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.IsSeenOrMarkAsync(string.Empty, default));
    }

    [Fact]
    public void Ctor_RejectsNonPositiveTtl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryActivityIdStore(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryActivityIdStore(-1));
    }

    [Fact]
    public async Task IsSeenOrMarkAsync_HonoursCancellation()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.IsSeenOrMarkAsync("x", cts.Token));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var store = new InMemoryActivityIdStore(ttlMinutes: 10);

        store.Dispose();
        store.Dispose();
    }
}
