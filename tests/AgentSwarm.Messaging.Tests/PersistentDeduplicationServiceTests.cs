// -----------------------------------------------------------------------
// <copyright file="PersistentDeduplicationServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 4.3 — round-trip tests for
/// <see cref="PersistentDeduplicationService"/> against an in-memory
/// SQLite connection using the real <see cref="MessagingDbContext"/>
/// schema. Pins the three brief scenarios:
/// <list type="bullet">
///   <item><description>"First event processed" — IsProcessedAsync is false
///   before MarkProcessedAsync, true after.</description></item>
///   <item><description>Concurrent TryReserveAsync — exactly one winner
///   among N callers for the same event id (the atomic-claim contract
///   inherited from the Stage 2.2 brief).</description></item>
///   <item><description>Sticky-processed — ReleaseReservationAsync is a
///   no-op after MarkProcessedAsync.</description></item>
/// </list>
/// </summary>
public sealed class PersistentDeduplicationServiceTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private ServiceProvider _provider = null!;
    private FakeTimeProvider _time = null!;
    private PersistentDeduplicationService _service = null!;

    public async Task InitializeAsync()
    {
        // Per-fixture shared-cache in-memory SQLite database.
        // Iter-3 evaluator fallout — the prior `:memory:` + single
        // shared SqliteConnection pattern is not safe for the
        // TryReserveAsync_ConcurrentCallers_OnlyOneWins fact
        // (16 concurrent DbContexts hitting one connection trips
        // SqliteConnection.RemoveCommand NREs). Shared-cache mode
        // (`Mode=Memory;Cache=Shared`) lets each DbContext open its
        // own SqliteConnection against the same in-memory database;
        // the `_keepAlive` connection keeps the shared DB alive for
        // the fixture lifetime.
        _connectionString = $"DataSource=dedup-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connectionString));
        _provider = services.BuildServiceProvider();

        await using (var scope = _provider.CreateAsyncScope())
        await using (var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        _service = new PersistentDeduplicationService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            NullLogger<PersistentDeduplicationService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task IsProcessedAsync_ReturnsFalse_BeforeMarkProcessed_AndTrue_After()
    {
        // Brief scenario 1: "First event processed — Given event ID
        // evt-1 has not been seen, When IsProcessedAsync is called,
        // Then it returns false; after MarkProcessedAsync, a second
        // call returns true."
        (await _service.IsProcessedAsync("evt-1", CancellationToken.None))
            .Should().BeFalse("nothing has been reserved or marked for evt-1 yet");

        await _service.MarkProcessedAsync("evt-1", CancellationToken.None);

        (await _service.IsProcessedAsync("evt-1", CancellationToken.None))
            .Should().BeTrue("MarkProcessedAsync writes the sticky processed marker");
    }

    [Fact]
    public async Task TryReserveAsync_ReturnsTrueOnce_AndFalseForDuplicateCallers()
    {
        var first = await _service.TryReserveAsync("evt-2", CancellationToken.None);
        var second = await _service.TryReserveAsync("evt-2", CancellationToken.None);
        var third = await _service.TryReserveAsync("evt-2", CancellationToken.None);

        first.Should().BeTrue("the first caller wins the atomic-claim race");
        second.Should().BeFalse("a duplicate reservation must be suppressed");
        third.Should().BeFalse("the suppression is sticky for the entire reservation lifetime");
    }

    [Fact]
    public async Task ReleaseReservationAsync_AllowsRetryAfterCaughtException()
    {
        // Stage 2.2 brief Scenario 4 invariant: a caught handler
        // exception releases the reservation so a live re-delivery
        // is processed normally.
        (await _service.TryReserveAsync("evt-3", CancellationToken.None)).Should().BeTrue();
        await _service.ReleaseReservationAsync("evt-3", CancellationToken.None);
        (await _service.TryReserveAsync("evt-3", CancellationToken.None)).Should().BeTrue(
            "after a release the next live re-delivery's TryReserveAsync must succeed");
    }

    [Fact]
    public async Task ReleaseReservationAsync_IsNoOp_AfterMarkProcessed()
    {
        // Sticky-processed guard: once MarkProcessedAsync has run, a
        // release call MUST NOT re-open the gate for a fully-
        // processed event id.
        (await _service.TryReserveAsync("evt-4", CancellationToken.None)).Should().BeTrue();
        await _service.MarkProcessedAsync("evt-4", CancellationToken.None);
        await _service.ReleaseReservationAsync("evt-4", CancellationToken.None);

        (await _service.IsProcessedAsync("evt-4", CancellationToken.None))
            .Should().BeTrue("MarkProcessedAsync marker is sticky and survives a stray release call");
        (await _service.TryReserveAsync("evt-4", CancellationToken.None))
            .Should().BeFalse("the gate stays closed for an already-processed event id");
    }

    [Fact]
    public async Task ReleaseReservationAsync_IsNoOp_WhenEventNeverReserved()
    {
        // Idempotency / safety: per the IDeduplicationService XML
        // contract a release call for a never-reserved id must
        // succeed without side effects.
        var act = async () => await _service.ReleaseReservationAsync("evt-unknown", CancellationToken.None);
        await act.Should().NotThrowAsync();

        (await _service.IsProcessedAsync("evt-unknown", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task MarkProcessedAsync_WithoutPriorReservation_InsertsProcessedRow()
    {
        // Tooling / replay path: MarkProcessedAsync called WITHOUT a
        // prior TryReserveAsync must still write the sticky processed
        // marker so a subsequent reservation is blocked.
        await _service.MarkProcessedAsync("evt-5", CancellationToken.None);

        (await _service.IsProcessedAsync("evt-5", CancellationToken.None)).Should().BeTrue();
        (await _service.TryReserveAsync("evt-5", CancellationToken.None))
            .Should().BeFalse("a previously-marked event id must not be re-claimable");
    }

    [Fact]
    public async Task MarkProcessedAsync_IsIdempotent_OnDuplicateCalls()
    {
        await _service.TryReserveAsync("evt-6", CancellationToken.None);
        await _service.MarkProcessedAsync("evt-6", CancellationToken.None);

        var first = await ReadProcessedAtAsync("evt-6");

        // Advancing time and re-marking must not bump ProcessedAt — a
        // hot duplicate-storm event would otherwise never age out of
        // the sliding window (the cleanup sweep relies on the
        // initial mark time as the TTL handle).
        _time.Advance(TimeSpan.FromMinutes(30));
        await _service.MarkProcessedAsync("evt-6", CancellationToken.None);

        var second = await ReadProcessedAtAsync("evt-6");
        second.Should().Be(first, "MarkProcessedAsync must not refresh the sliding-window TTL handle on duplicate calls");
    }

    [Fact]
    public async Task TryReserveAsync_ThrowsForNullOrEmptyId()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.TryReserveAsync(string.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.TryReserveAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task TryReserveAsync_ConcurrentCallers_OnlyOneWins()
    {
        // Acceptance criterion from the story: "Duplicate webhook
        // delivery does not execute the same human command twice."
        // The atomic-claim contract requires exactly one winner among
        // any number of concurrent callers for the same event id.
        const int CallerCount = 16;
        var barrier = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, CallerCount).Select(async _ =>
        {
            await barrier.Task;
            return await _service.TryReserveAsync("evt-concurrent", CancellationToken.None);
        }).ToArray();

        barrier.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1, "exactly one concurrent caller wins the atomic reservation");
        results.Count(r => !r).Should().Be(CallerCount - 1);
    }

    private async Task<DateTime?> ReadProcessedAtAsync(string eventId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.ProcessedEvents.AsNoTracking().SingleAsync(x => x.EventId == eventId);
        return row.ProcessedAt;
    }
}
