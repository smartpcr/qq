using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Cards;

/// <summary>
/// Tests for <see cref="ProcessedCardActionEvictionService"/> — the Stage 6.2 background
/// hosted service that prunes expired entries from the in-memory processed-action set.
/// Uses real <see cref="TimeProvider.System"/> with deliberately short intervals so the
/// eviction loop fires within the test's deadline.
/// </summary>
public sealed class ProcessedCardActionEvictionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_EvictsEntriesPastLifetime_OnTickInterval()
    {
        // Stage 6.2 test scenario: "Expired processed-action entries cleaned — Given
        // an in-memory processed-action entry with age older than the lifetime, When
        // the cleanup background job runs, Then the entry is removed from the set."
        var options = new CardActionDedupeOptions
        {
            EntryLifetime = TimeSpan.FromMilliseconds(50),
            EvictionInterval = TimeSpan.FromMilliseconds(75),
        };
        var set = new ProcessedCardActionSet(options, TimeProvider.System);
        Assert.True(set.TryClaim(("q-stale", "user-1"), out _));

        var service = new ProcessedCardActionEvictionService(
            set, options, TimeProvider.System, NullLogger<ProcessedCardActionEvictionService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Wait long enough for the entry to age past the lifetime AND for at least one
        // eviction tick to fire (and a generous safety margin so CI scheduler delays
        // do not cause flakes).
        await WaitForAsync(() => set.Count == 0, TimeSpan.FromSeconds(5));

        Assert.Equal(0, set.Count);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_KeepsFreshEntries()
    {
        // Lifetime >> eviction tick — fresh entries must survive multiple ticks.
        var options = new CardActionDedupeOptions
        {
            EntryLifetime = TimeSpan.FromSeconds(30),
            EvictionInterval = TimeSpan.FromMilliseconds(25),
        };
        var set = new ProcessedCardActionSet(options, TimeProvider.System);
        Assert.True(set.TryClaim(("q-fresh", "user-1"), out _));

        var service = new ProcessedCardActionEvictionService(
            set, options, TimeProvider.System, NullLogger<ProcessedCardActionEvictionService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Let the eviction service tick several times.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, set.Count);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveEvictionInterval()
    {
        var options = new CardActionDedupeOptions { EvictionInterval = TimeSpan.Zero };
        Assert.Throws<ArgumentException>(() => new ProcessedCardActionEvictionService(
            new ProcessedCardActionSet(),
            options,
            TimeProvider.System,
            NullLogger<ProcessedCardActionEvictionService>.Instance));
    }

    [Fact]
    public async Task StopAsync_ExitsLoopPromptly()
    {
        var options = new CardActionDedupeOptions
        {
            EntryLifetime = TimeSpan.FromHours(24),
            EvictionInterval = TimeSpan.FromHours(1),
        };
        var set = new ProcessedCardActionSet(options, TimeProvider.System);
        var service = new ProcessedCardActionEvictionService(
            set, options, TimeProvider.System, NullLogger<ProcessedCardActionEvictionService>.Instance);

        await service.StartAsync(CancellationToken.None);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(stopCts.Token);
        // No assertion — reaching here without a TaskCanceledException is the success
        // signal (the BackgroundService exited within the deadline).
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }
    }
}

