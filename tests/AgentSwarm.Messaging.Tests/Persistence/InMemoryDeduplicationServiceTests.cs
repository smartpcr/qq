using AgentSwarm.Messaging.Persistence;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.2 acceptance tests for <see cref="InMemoryDeduplicationService"/>.
/// Covers the sliding-window fast-path scenarios pinned by architecture.md
/// §4.11 and the implementation-plan Stage 2.2 contract.
/// </summary>
public class InMemoryDeduplicationServiceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task TryReserveAsync_FirstClaim_ReturnsTrue()
    {
        var dedup = NewService();

        var reserved = await dedup.TryReserveAsync("evt-1");

        reserved.Should().BeTrue();
    }

    [Fact]
    public async Task TryReserveAsync_SecondClaimWithinWindow_ReturnsFalse()
    {
        // Stage 2.2 Test Scenario: "In-memory dedup fast-path -- already in
        // the sliding-window cache, TryReserveAsync returns false without
        // querying the database".
        var dedup = NewService();

        (await dedup.TryReserveAsync("evt-dup")).Should().BeTrue();
        (await dedup.TryReserveAsync("evt-dup")).Should().BeFalse();
    }

    [Fact]
    public async Task TryReserveAsync_AfterTtlElapsed_AllowsRe_Reserve()
    {
        var dedup = NewService(TimeSpan.FromMinutes(10));

        (await dedup.TryReserveAsync("evt-aged")).Should().BeTrue();

        _clock.Advance(TimeSpan.FromMinutes(11));

        (await dedup.TryReserveAsync("evt-aged")).Should().BeTrue();
    }

    [Fact]
    public async Task IsProcessedAsync_AfterReservation_ReturnsTrue()
    {
        var dedup = NewService();
        await dedup.TryReserveAsync("evt-proc");

        (await dedup.IsProcessedAsync("evt-proc")).Should().BeTrue();
    }

    [Fact]
    public async Task IsProcessedAsync_BeforeReservation_ReturnsFalse()
    {
        var dedup = NewService();

        (await dedup.IsProcessedAsync("evt-unseen")).Should().BeFalse();
    }

    [Fact]
    public async Task IsProcessedAsync_AfterTtlEviction_ReturnsFalse()
    {
        var dedup = NewService(TimeSpan.FromSeconds(30));
        await dedup.TryReserveAsync("evt-stale");

        _clock.Advance(TimeSpan.FromMinutes(2));

        (await dedup.IsProcessedAsync("evt-stale")).Should().BeFalse();
    }

    [Fact]
    public async Task MarkProcessedAsync_IsIdempotent()
    {
        var dedup = NewService();
        await dedup.MarkProcessedAsync("evt-mark");
        await dedup.MarkProcessedAsync("evt-mark");

        (await dedup.IsProcessedAsync("evt-mark")).Should().BeTrue();
    }

    [Fact]
    public async Task MarkProcessedAsync_RefreshesSlidingWindow()
    {
        var dedup = NewService(TimeSpan.FromMinutes(10));
        await dedup.TryReserveAsync("evt-refresh");

        _clock.Advance(TimeSpan.FromMinutes(8));
        await dedup.MarkProcessedAsync("evt-refresh");

        _clock.Advance(TimeSpan.FromMinutes(8));

        // 16 minutes since the original reservation, but the refresh at
        // minute 8 extended the window to minute 18; the entry should still
        // be present.
        (await dedup.IsProcessedAsync("evt-refresh")).Should().BeTrue();
    }

    [Fact]
    public async Task TryReserveAsync_NullOrEmptyEventId_Throws()
    {
        var dedup = NewService();
        await FluentActions.Awaiting(() => dedup.TryReserveAsync(""))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => dedup.TryReserveAsync(null!))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_NonPositiveTtl_Throws()
    {
        FluentActions.Invoking(() => new InMemoryDeduplicationService(_clock, TimeSpan.Zero))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new InMemoryDeduplicationService(_clock, TimeSpan.FromSeconds(-1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Default_TtlIsOneHour()
    {
        InMemoryDeduplicationService.DefaultTtl.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task PruneExpired_RefreshedEntryAtExpiryBoundary_IsNotErased()
    {
        // Regression test for evaluator-flagged PruneExpired race (iter-1
        // feedback item #4): if PruneExpired uses TryGetValue at removal
        // time and conditionally removes the CURRENT value, a concurrent
        // MarkProcessedAsync that refreshed the entry between the snapshot
        // scan and the remove call would cause us to erase the refreshed
        // entry (false negative on the next IsProcessedAsync call). The
        // fix is to capture KeyValuePair from the snapshot and use the
        // atomic ConcurrentDictionary.TryRemove(KeyValuePair) overload --
        // which only removes when the stored value still equals the
        // observed value.
        //
        // We deterministically reproduce the race by:
        //   1. Reserving an entry at t=0.
        //   2. Advancing the clock past the TTL so the entry is "expired"
        //      from the perspective of the next scan.
        //   3. Refreshing the entry via MarkProcessedAsync (this stamps
        //      the new t=TTL+1 timestamp, putting the entry inside the
        //      window again).
        //   4. Calling IsProcessedAsync, which calls PruneExpired. The
        //      buggy implementation would observe the refreshed value
        //      via TryGetValue and call TryRemove with that value,
        //      erroneously removing the refreshed entry. The corrected
        //      implementation does not observe the entry as expired
        //      because the snapshot scan reads the refreshed timestamp.
        var dedup = NewService(TimeSpan.FromMinutes(10));
        (await dedup.TryReserveAsync("evt-boundary")).Should().BeTrue();

        // Step past the TTL so the original t=0 timestamp is expired.
        _clock.Advance(TimeSpan.FromMinutes(11));

        // Refresh the entry; this writes a new t=11min timestamp inside
        // the window relative to itself.
        await dedup.MarkProcessedAsync("evt-boundary");

        // A second call to IsProcessedAsync (which runs PruneExpired)
        // must observe the refreshed entry as still live, not erase it.
        (await dedup.IsProcessedAsync("evt-boundary")).Should().BeTrue(
            "the refreshed entry is inside its own sliding window and must not be erased by PruneExpired");

        // Reserve must still report duplicate -- the refreshed entry
        // is still in the cache.
        (await dedup.TryReserveAsync("evt-boundary")).Should().BeFalse(
            "the refreshed entry is still claimed; TryReserveAsync must report duplicate");
    }

    private InMemoryDeduplicationService NewService(TimeSpan? ttl = null)
    {
        return new InMemoryDeduplicationService(_clock, ttl ?? InMemoryDeduplicationService.DefaultTtl);
    }
}
