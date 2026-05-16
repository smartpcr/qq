// -----------------------------------------------------------------------
// <copyright file="SlidingWindowDeduplicationServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 4.3 — exercises the in-memory sliding-window variant
/// <see cref="SlidingWindowDeduplicationService"/>. Mirrors the
/// brief scenarios for the dev backend so the
/// ConcurrentDictionary-with-cleanup-timer implementation observes
/// the same contract as the EF-backed
/// <see cref="PersistentDeduplicationService"/>.
/// </summary>
public sealed class SlidingWindowDeduplicationServiceTests
{
    private static SlidingWindowDeduplicationService CreateSut(
        FakeTimeProvider time,
        TimeSpan? ttl = null,
        TimeSpan? purge = null)
    {
        var options = Options.Create(new DeduplicationOptions
        {
            EntryTimeToLive = ttl ?? TimeSpan.FromHours(1),
            // Default purge=TimeSpan.Zero disables the timer; tests
            // drive Purge() manually for determinism.
            PurgeInterval = purge ?? TimeSpan.Zero,
        });
        return new SlidingWindowDeduplicationService(
            options,
            time,
            NullLogger<SlidingWindowDeduplicationService>.Instance);
    }

    [Fact]
    public async Task IsProcessedAsync_ReturnsFalse_BeforeMarkProcessed_AndTrue_After()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateSut(time);

        (await sut.IsProcessedAsync("evt-1", CancellationToken.None)).Should().BeFalse();
        await sut.MarkProcessedAsync("evt-1", CancellationToken.None);
        (await sut.IsProcessedAsync("evt-1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task TryReserveAsync_AwardsExactlyOneConcurrentCaller()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateSut(time);

        const int CallerCount = 32;
        var barrier = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, CallerCount).Select(async _ =>
        {
            await barrier.Task;
            return await sut.TryReserveAsync("evt-concurrent", CancellationToken.None);
        }).ToArray();
        barrier.SetResult();

        var results = await Task.WhenAll(tasks);
        results.Count(r => r).Should().Be(1, "the atomic-claim contract awards exactly one concurrent winner");
    }

    [Fact]
    public async Task ReleaseReservationAsync_NoOp_AfterMarkProcessed()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateSut(time);

        (await sut.TryReserveAsync("evt-2", CancellationToken.None)).Should().BeTrue();
        await sut.MarkProcessedAsync("evt-2", CancellationToken.None);
        await sut.ReleaseReservationAsync("evt-2", CancellationToken.None);

        (await sut.IsProcessedAsync("evt-2", CancellationToken.None)).Should().BeTrue();
        (await sut.TryReserveAsync("evt-2", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Purge_EvictsExpiredEntries()
    {
        // Brief scenario 2: "Expired events evicted — Given event
        // evt-old was processed 2 hours ago and TTL is 1 hour, When
        // cleanup runs, Then IsProcessedAsync("evt-old") returns
        // false."
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        using var sut = CreateSut(time, ttl: TimeSpan.FromHours(1));

        await sut.MarkProcessedAsync("evt-old", CancellationToken.None);
        (await sut.IsProcessedAsync("evt-old", CancellationToken.None)).Should().BeTrue();

        time.Advance(TimeSpan.FromHours(2));
        var evicted = sut.Purge();

        evicted.Should().BeGreaterThanOrEqualTo(1);
        (await sut.IsProcessedAsync("evt-old", CancellationToken.None))
            .Should().BeFalse("expired entries must drop out of the sliding window after the purge");
    }

    [Fact]
    public async Task Purge_DoesNotEvictEntriesWithinTtl()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        using var sut = CreateSut(time, ttl: TimeSpan.FromHours(1));

        await sut.MarkProcessedAsync("evt-fresh", CancellationToken.None);

        // 30 minutes < 1 hour TTL → must NOT evict.
        time.Advance(TimeSpan.FromMinutes(30));
        sut.Purge();

        (await sut.IsProcessedAsync("evt-fresh", CancellationToken.None))
            .Should().BeTrue("entries inside the TTL window must survive the purge");
    }

    [Fact]
    public async Task Purge_EvictsStaleReservations_NotJustProcessed()
    {
        // A reservation that was never released and never promoted
        // would leak indefinitely without an eviction path; the
        // sweep uses COALESCE(processed_at, reserved_at) so an
        // abandoned reservation ages out on the same TTL.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        using var sut = CreateSut(time, ttl: TimeSpan.FromHours(1));

        await sut.TryReserveAsync("evt-stale", CancellationToken.None);
        time.Advance(TimeSpan.FromHours(2));
        sut.Purge();

        // After eviction, the slot is reclaimable.
        (await sut.TryReserveAsync("evt-stale", CancellationToken.None))
            .Should().BeTrue("stale reservations must evict on the TTL so the slot is reclaimable");
    }

    [Fact]
    public async Task TryReserveAsync_AndReleaseReservation_RoundTrip()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateSut(time);

        (await sut.TryReserveAsync("evt-3", CancellationToken.None)).Should().BeTrue();
        await sut.ReleaseReservationAsync("evt-3", CancellationToken.None);
        (await sut.TryReserveAsync("evt-3", CancellationToken.None))
            .Should().BeTrue("a released reservation makes the slot reclaimable");
    }

    [Fact]
    public async Task Constructor_RegistersCleanupTimer_WhenPurgeIntervalGreaterThanZero()
    {
        var time = new FakeTimeProvider();
        using var sut = CreateSut(time, purge: TimeSpan.FromMinutes(5));

        // The constructor must create the timer; advancing the fake
        // clock past the configured period exercises the timer
        // callback path without depending on wall-clock waits. We
        // verify the side effect (purge ran) by populating an old
        // entry, advancing past the TTL, then advancing one purge
        // tick — the entry should be evicted by the timer callback.
        await sut.MarkProcessedAsync("evt-timer", CancellationToken.None);
        time.Advance(TimeSpan.FromHours(2));
        time.Advance(TimeSpan.FromMinutes(5));

        (await sut.IsProcessedAsync("evt-timer", CancellationToken.None))
            .Should().BeFalse("the periodic cleanup timer must purge expired entries without manual intervention");
    }
}
