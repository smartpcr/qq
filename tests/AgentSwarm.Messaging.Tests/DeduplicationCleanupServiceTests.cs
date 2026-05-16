// -----------------------------------------------------------------------
// <copyright file="DeduplicationCleanupServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 4.3 — direct unit tests for
/// <see cref="DeduplicationCleanupService.RunSweepAsync"/> using a
/// real SQLite in-memory database. Verifies the eviction predicate
/// matches the persistent store's row shape and that the sweep
/// removes only rows whose effective timestamp is older than the
/// configured TTL.
/// </summary>
public sealed class DeduplicationCleanupServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private FakeTimeProvider _time = null!;
    private PersistentDeduplicationService _store = null!;
    private DeduplicationCleanupService _cleanup = null!;
    private DeduplicationOptions _options = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using (var scope = _provider.CreateAsyncScope())
        await using (var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        _options = new DeduplicationOptions
        {
            EntryTimeToLive = TimeSpan.FromHours(1),
            // Disable the loop — tests drive RunSweepAsync directly.
            PurgeInterval = TimeSpan.Zero,
        };
        var monitor = new TestOptionsMonitor<DeduplicationOptions>(_options);

        _store = new PersistentDeduplicationService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            NullLogger<PersistentDeduplicationService>.Instance);
        _cleanup = new DeduplicationCleanupService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            monitor,
            _time,
            NullLogger<DeduplicationCleanupService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RunSweepAsync_RemovesEntriesOlderThanTtl()
    {
        // Brief scenario: "Expired events evicted — Given event
        // evt-old was processed 2 hours ago and TTL is 1 hour,
        // When cleanup runs, Then IsProcessedAsync('evt-old')
        // returns false."
        await _store.MarkProcessedAsync("evt-old", CancellationToken.None);
        (await _store.IsProcessedAsync("evt-old", CancellationToken.None)).Should().BeTrue();

        _time.Advance(TimeSpan.FromHours(2));

        var evicted = await _cleanup.RunSweepAsync(CancellationToken.None);
        evicted.Should().BeGreaterThanOrEqualTo(1);

        (await _store.IsProcessedAsync("evt-old", CancellationToken.None))
            .Should().BeFalse("the sliding-window sweep must evict the expired processed row");
    }

    [Fact]
    public async Task RunSweepAsync_KeepsEntriesWithinTtl()
    {
        await _store.MarkProcessedAsync("evt-fresh", CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(30));
        var evicted = await _cleanup.RunSweepAsync(CancellationToken.None);
        evicted.Should().Be(0);

        (await _store.IsProcessedAsync("evt-fresh", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RunSweepAsync_EvictsAbandonedReservations()
    {
        // A handler that crashed without releasing leaves a row with
        // ProcessedAt=NULL. The cleanup sweep uses
        // COALESCE(ProcessedAt, ReservedAt) so the abandoned
        // reservation ages out on the same TTL as a normal processed
        // row.
        (await _store.TryReserveAsync("evt-stale", CancellationToken.None)).Should().BeTrue();

        _time.Advance(TimeSpan.FromHours(2));
        var evicted = await _cleanup.RunSweepAsync(CancellationToken.None);
        evicted.Should().BeGreaterThanOrEqualTo(1);

        (await _store.TryReserveAsync("evt-stale", CancellationToken.None))
            .Should().BeTrue("the slot must be reclaimable after the abandoned reservation ages out");
    }

    [Fact]
    public async Task RunSweepAsync_ReturnsZero_WhenTtlIsZeroOrNegative()
    {
        _options.EntryTimeToLive = TimeSpan.Zero;

        await _store.MarkProcessedAsync("evt-noop", CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(2));
        var evicted = await _cleanup.RunSweepAsync(CancellationToken.None);

        evicted.Should().Be(0, "a non-positive TTL disables the sweep");
        (await _store.IsProcessedAsync("evt-noop", CancellationToken.None)).Should().BeTrue();
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
