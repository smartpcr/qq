using AgentSwarm.Messaging.Teams.Storage;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class InMemoryActivityIdStoreTests
{
    private static IOptionsMonitor<TeamsMessagingOptions> Monitor(int ttlMinutes = 10)
    {
        var opts = new TeamsMessagingOptions { DeduplicationTtlMinutes = ttlMinutes };
        return new TestMonitor(opts);
    }

    [Fact]
    public async Task FirstMark_Returns_False_Then_Duplicate_Returns_True()
    {
        using var store = new InMemoryActivityIdStore(Monitor(), clock: null, enableBackgroundEviction: false);
        Assert.False(await store.IsSeenOrMarkAsync("a1", default));
        Assert.True(await store.IsSeenOrMarkAsync("a1", default));
    }

    [Fact]
    public async Task Distinct_Ids_Are_Independent()
    {
        using var store = new InMemoryActivityIdStore(Monitor(), clock: null, enableBackgroundEviction: false);
        Assert.False(await store.IsSeenOrMarkAsync("a1", default));
        Assert.False(await store.IsSeenOrMarkAsync("a2", default));
        Assert.True(await store.IsSeenOrMarkAsync("a1", default));
    }

    [Fact]
    public async Task Empty_ActivityId_Throws()
    {
        using var store = new InMemoryActivityIdStore(Monitor(), clock: null, enableBackgroundEviction: false);
        await Assert.ThrowsAsync<ArgumentException>(() => store.IsSeenOrMarkAsync(" ", default));
    }

    [Fact]
    public async Task Evict_Drops_Entries_Older_Than_TTL()
    {
        var now = DateTimeOffset.UtcNow;
        var clockValue = now;
        using var store = new InMemoryActivityIdStore(Monitor(ttlMinutes: 1), clock: () => clockValue, enableBackgroundEviction: false);

        Assert.False(await store.IsSeenOrMarkAsync("old", default));
        clockValue = now.AddMinutes(2);
        store.Evict();

        Assert.False(await store.IsSeenOrMarkAsync("old", default));
    }

    private sealed class TestMonitor : IOptionsMonitor<TeamsMessagingOptions>
    {
        public TestMonitor(TeamsMessagingOptions value) { CurrentValue = value; }
        public TeamsMessagingOptions CurrentValue { get; }
        public TeamsMessagingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TeamsMessagingOptions, string?> listener) => null;
    }
}
