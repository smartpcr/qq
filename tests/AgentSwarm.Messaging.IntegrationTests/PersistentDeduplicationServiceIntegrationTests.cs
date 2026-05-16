// -----------------------------------------------------------------------
// <copyright file="PersistentDeduplicationServiceIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.IntegrationTests;

/// <summary>
/// Stage 4.3 — exercises the EF-backed
/// <see cref="PersistentDeduplicationService"/> end-to-end through the
/// production DI seam (<see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>)
/// against a real shared-cache SQLite database. Without this
/// integration test a regression in the migration, the entity
/// configuration, the <see cref="MessagingDbContext"/> wire-up, or the
/// service replacement would pass unit tests but silently re-process
/// duplicate webhook deliveries in production.
/// </summary>
public sealed class PersistentDeduplicationServiceIntegrationTests : IDisposable
{
    private readonly IHost _host;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqliteConnection _keepAlive;

    public PersistentDeduplicationServiceIntegrationTests()
    {
        var dbName = $"persistent-dedup-test-{Guid.NewGuid():N}";
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MessagingDb"] = connectionString,
                ["MessagingDb:UseMigrations"] = "false",
                // Keep the sweep loop quiet so it does not interfere
                // with deterministic test assertions; tests drive
                // DeduplicationCleanupService.RunSweepAsync directly
                // when they need eviction behaviour.
                ["Deduplication:PurgeInterval"] = "00:00:00",
                ["Deduplication:EntryTimeToLive"] = "01:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMessagingPersistence(configuration);

        _host = new HostBuilder()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services)
                {
                    s.Add(descriptor);
                }
            })
            .Build();

        _host.StartAsync().GetAwaiter().GetResult();
        _scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task TryReserveAsync_PersistsRowThatSurvivesScopeBoundary()
    {
        // A reservation written by the singleton dedup service must
        // be visible to a freshly-resolved scope so duplicate
        // suppression actually crosses request boundaries.
        var dedup = _host.Services.GetRequiredService<IDeduplicationService>();

        (await dedup.TryReserveAsync("evt-cross-scope", CancellationToken.None)).Should().BeTrue();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.ProcessedEvents.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == "evt-cross-scope");
        row.Should().NotBeNull("the reservation row must be persisted across DI scope boundaries");
        row!.ProcessedAt.Should().BeNull("a bare reservation has no ProcessedAt marker yet");
    }

    [Fact]
    public async Task DuplicateWebhookDelivery_DoesNotProcessTwice()
    {
        // Brief scenario: "Webhook dedup integration — Given a
        // Telegram Update with Id=42 is received and processed, When
        // the same Id=42 arrives again, Then the webhook returns 200
        // but no downstream processing occurs."
        var dedup = _host.Services.GetRequiredService<IDeduplicationService>();

        (await dedup.TryReserveAsync("42", CancellationToken.None))
            .Should().BeTrue("first delivery wins the atomic claim");
        await dedup.MarkProcessedAsync("42", CancellationToken.None);

        (await dedup.TryReserveAsync("42", CancellationToken.None))
            .Should().BeFalse("a duplicate Telegram Update id must be short-circuited at the dedup gate");
        (await dedup.IsProcessedAsync("42", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseAfterCaughtException_AllowsLiveReDelivery()
    {
        // Stage 2.2 brief Scenario 4: a caught handler exception
        // releases the reservation so a live re-delivery is
        // processed normally.
        var dedup = _host.Services.GetRequiredService<IDeduplicationService>();

        (await dedup.TryReserveAsync("evt-release", CancellationToken.None)).Should().BeTrue();
        await dedup.ReleaseReservationAsync("evt-release", CancellationToken.None);
        (await dedup.TryReserveAsync("evt-release", CancellationToken.None))
            .Should().BeTrue("the second live delivery must succeed after a release");

        // The released row should be gone from the database — a
        // released reservation must not linger as a stale entry that
        // could trip the cleanup sweep into evicting a row that
        // was just reclaimed.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var rowCount = await db.ProcessedEvents.AsNoTracking().CountAsync(x => x.EventId == "evt-release");
        rowCount.Should().Be(1,
            "after release+re-reserve exactly one row should remain (the new reservation, not a stale leftover)");
    }

    [Fact]
    public void AddMessagingPersistence_ReplacesDeduplicationServiceWithPersistentImplementation()
    {
        // Regression guard mirroring the other persistent-store
        // integration tests: a refactor that switched the
        // services.Replace(...) call to TryAddSingleton(...) would
        // silently leave production using the in-memory stub and
        // lose dedup state on every worker restart. Pinning the
        // resolved concrete type at the integration-test level
        // catches that.
        var resolved = _host.Services.GetRequiredService<IDeduplicationService>();
        resolved.GetType().FullName.Should().Be(
            "AgentSwarm.Messaging.Persistence.PersistentDeduplicationService",
            "AddMessagingPersistence MUST replace any in-memory IDeduplicationService with the EF-backed PersistentDeduplicationService");
    }

    [Fact]
    public void AddMessagingPersistence_RegistersDeduplicationCleanupHostedService()
    {
        // The hosted service is required by the brief's "periodic
        // purge of expired entries" contract; without it the
        // processed_events table would grow without bound.
        var hosted = _host.Services.GetServices<IHostedService>();
        hosted.Should().Contain(h => h is DeduplicationCleanupService,
            "AddMessagingPersistence MUST register DeduplicationCleanupService as a hosted service so the periodic purge runs in production");
    }

    [Fact]
    public void AddMessagingPersistence_BindsDeduplicationOptions()
    {
        var options = _host.Services.GetRequiredService<IOptions<DeduplicationOptions>>().Value;
        options.EntryTimeToLive.Should().Be(TimeSpan.FromHours(1));
        options.PurgeInterval.Should().Be(TimeSpan.Zero,
            "test fixture pinned PurgeInterval=00:00:00 to disable the loop for deterministic assertions");
    }

    [Fact]
    public async Task CleanupSweep_RemovesExpiredEntries()
    {
        // Drive the cleanup sweep directly via DI to confirm the EF
        // query path works against the real schema.
        var dedup = _host.Services.GetRequiredService<IDeduplicationService>();
        await dedup.MarkProcessedAsync("evt-evictable", CancellationToken.None);

        // Backdate the row past the TTL by writing the timestamps
        // directly through EF. The cleanup service uses the
        // ambient TimeProvider, so the simplest way to assert
        // eviction is to push the row's stored timestamp into the
        // past.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await db.ProcessedEvents.SingleAsync(x => x.EventId == "evt-evictable");
            row.ReservedAt = DateTime.UtcNow - TimeSpan.FromHours(3);
            row.ProcessedAt = DateTime.UtcNow - TimeSpan.FromHours(3);
            await db.SaveChangesAsync();
        }

        var cleanup = (DeduplicationCleanupService)_host.Services
            .GetServices<IHostedService>()
            .First(h => h is DeduplicationCleanupService);

        var evicted = await cleanup.RunSweepAsync(CancellationToken.None);
        evicted.Should().BeGreaterThanOrEqualTo(1);

        (await dedup.IsProcessedAsync("evt-evictable", CancellationToken.None)).Should().BeFalse();
    }

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        _keepAlive.Dispose();
    }
}
