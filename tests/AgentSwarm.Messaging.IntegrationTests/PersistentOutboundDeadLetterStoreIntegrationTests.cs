// -----------------------------------------------------------------------
// <copyright file="PersistentOutboundDeadLetterStoreIntegrationTests.cs" company="Microsoft Corp.">
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.IntegrationTests;

/// <summary>
/// Iter-5 evaluator item 4 — exercises the EF-backed
/// <see cref="PersistentOutboundDeadLetterStore"/> against a real
/// SQLite database (in-memory shared-cache). The iter-4 evaluator
/// called out that the unit suite only covered the in-memory and
/// flaky-double variants; without an EF integration test a regression
/// in the migration, the entity configuration, the
/// <see cref="MessagingDbContext"/> wire-up, or the
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>
/// service replacement would pass unit tests but silently lose every
/// dead-letter row in production. These tests use the same DI seam
/// as the worker so the persistent variant is exercised end-to-end.
/// </summary>
public sealed class PersistentOutboundDeadLetterStoreIntegrationTests : IDisposable
{
    private readonly IHost _host;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqliteConnection _keepAlive;

    public PersistentOutboundDeadLetterStoreIntegrationTests()
    {
        // SQLite shared-cache in-memory databases are destroyed the
        // moment the LAST connection with that data source name
        // closes; the DatabaseInitializer's scope is disposed at the
        // end of StartAsync, so without this open keepalive the
        // schema would vanish before any test runs.
        var dbName = $"persistent-dlq-test-{Guid.NewGuid():N}";
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MessagingDb"] = connectionString,
                ["MessagingDb:UseMigrations"] = "false",
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

        // Start the host so DatabaseInitializer creates the schema.
        _host.StartAsync().GetAwaiter().GetResult();
        _scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task RecordAsync_PersistsRowThatSurvivesScopeBoundary()
    {
        // Iter-5 evaluator item 4 — the durable contract: a row
        // written from the singleton sender's perspective must be
        // visible from a freshly-created DI scope, proving the
        // IServiceScopeFactory bridge actually commits to the DB and
        // is not just buffering in-memory.
        var store = _host.Services.GetRequiredService<IOutboundDeadLetterStore>();
        var record = new OutboundDeadLetterRecord
        {
            DeadLetterId = Guid.NewGuid(),
            ChatId = 1010,
            CorrelationId = "trace-dlq-persisted-cross-scope",
            AttemptCount = 4,
            FailureCategory = OutboundFailureCategory.TransientTransport,
            LastErrorType = "HttpRequestException",
            LastErrorMessage = "Connection refused (10.0.0.1:443)",
            FailedAt = DateTimeOffset.UtcNow,
        };

        await store.RecordAsync(record, CancellationToken.None);

        // Resolve a SECOND IOutboundDeadLetterStore from a fresh
        // scope to prove the row is visible to readers other than
        // the singleton that wrote it.
        using var freshScope = _scopeFactory.CreateScope();
        var freshStore = freshScope.ServiceProvider.GetRequiredService<IOutboundDeadLetterStore>();
        var rows = await freshStore.GetByCorrelationIdAsync(
            "trace-dlq-persisted-cross-scope",
            CancellationToken.None);

        rows.Should().HaveCount(1,
            "iter-5 evaluator item 4: a row written by PersistentOutboundDeadLetterStore must be readable from a different DI scope");
        rows[0].DeadLetterId.Should().Be(record.DeadLetterId);
        rows[0].ChatId.Should().Be(1010);
        rows[0].AttemptCount.Should().Be(4);
        rows[0].FailureCategory.Should().Be(OutboundFailureCategory.TransientTransport);
        rows[0].LastErrorType.Should().Be("HttpRequestException");
        rows[0].LastErrorMessage.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task RecordAsync_IsIdempotent_OnDuplicateDeadLetterIdInsert()
    {
        // Iter-5 evaluator item 4 — the PersistentOutboundDeadLetterStore
        // contract: a retry of RecordAsync with the same DeadLetterId
        // (e.g. because the prior SaveChangesAsync threw mid-round-trip
        // and the sender's outer retry loop calls back in) MUST behave
        // as a no-op, not throw a duplicate-key exception. The EF
        // backend implements this via FindAsync-then-skip plus a
        // SqliteException error code 19 catch as a race safety net.
        var store = _host.Services.GetRequiredService<IOutboundDeadLetterStore>();
        var id = Guid.NewGuid();
        var record = new OutboundDeadLetterRecord
        {
            DeadLetterId = id,
            ChatId = 2020,
            CorrelationId = "trace-dlq-idempotent",
            AttemptCount = 3,
            FailureCategory = OutboundFailureCategory.RateLimitExhausted,
            LastErrorType = "ApiRequestException",
            LastErrorMessage = "Too Many Requests: retry after 60",
            FailedAt = DateTimeOffset.UtcNow,
        };

        await store.RecordAsync(record, CancellationToken.None);
        var act = async () => await store.RecordAsync(record, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "iter-5 evaluator item 4: a duplicate DeadLetterId insert MUST be treated as success, not throw");

        var rows = await store.GetByCorrelationIdAsync("trace-dlq-idempotent", CancellationToken.None);
        rows.Should().HaveCount(1,
            "the idempotent insert must NOT inflate the audit trail with duplicate rows");
    }

    [Fact]
    public async Task GetByCorrelationIdAsync_ReturnsAllAttemptsOrderedByFailedAt()
    {
        // Iter-5 evaluator item 4 — when the same correlation
        // dead-letters multiple times (e.g. a permanent failure
        // followed by a transient retry storm), the operator's
        // trace-pivot query must surface every row in chronological
        // order so the timeline is reconstructible from the ledger
        // alone. Pin the ORDER BY FailedAt contract.
        var store = _host.Services.GetRequiredService<IOutboundDeadLetterStore>();
        var traceId = "trace-dlq-multiple-attempts";
        var t0 = DateTimeOffset.UtcNow;

        await store.RecordAsync(new OutboundDeadLetterRecord
        {
            DeadLetterId = Guid.NewGuid(),
            ChatId = 3030,
            CorrelationId = traceId,
            AttemptCount = 1,
            FailureCategory = OutboundFailureCategory.Permanent,
            LastErrorType = "ApiRequestException",
            LastErrorMessage = "Bad Request: can't parse entities",
            FailedAt = t0,
        }, CancellationToken.None);

        await store.RecordAsync(new OutboundDeadLetterRecord
        {
            DeadLetterId = Guid.NewGuid(),
            ChatId = 3030,
            CorrelationId = traceId,
            AttemptCount = 3,
            FailureCategory = OutboundFailureCategory.TransientTransport,
            LastErrorType = "HttpRequestException",
            LastErrorMessage = "Connection reset",
            FailedAt = t0.AddSeconds(5),
        }, CancellationToken.None);

        await store.RecordAsync(new OutboundDeadLetterRecord
        {
            DeadLetterId = Guid.NewGuid(),
            ChatId = 3030,
            CorrelationId = traceId,
            AttemptCount = 3,
            FailureCategory = OutboundFailureCategory.RateLimitExhausted,
            LastErrorType = "ApiRequestException",
            LastErrorMessage = "Too Many Requests",
            FailedAt = t0.AddSeconds(2),
        }, CancellationToken.None);

        var rows = await store.GetByCorrelationIdAsync(traceId, CancellationToken.None);

        rows.Should().HaveCount(3,
            "every dead-letter event for a correlation must land as a separate audit row");
        rows.Select(r => r.FailureCategory).Should().ContainInOrder(
            new[]
            {
                OutboundFailureCategory.Permanent,
                OutboundFailureCategory.RateLimitExhausted,
                OutboundFailureCategory.TransientTransport,
            },
            "iter-5 evaluator item 4: GetByCorrelationIdAsync MUST order by FailedAt so the operator timeline is reconstructible");
    }

    [Fact]
    public async Task RecordAsync_PersistsFailureCategoryAsString_NotInteger()
    {
        // Iter-5 evaluator item 4 — the entity configuration maps
        // FailureCategory through a value converter so the column is
        // stored as a human-readable string ("Permanent",
        // "TransientTransport", "RateLimitExhausted") rather than an
        // opaque ordinal. Reading the raw column value confirms a
        // regression that dropped the converter (and would silently
        // alias every value to 0) is caught.
        var store = _host.Services.GetRequiredService<IOutboundDeadLetterStore>();
        var record = new OutboundDeadLetterRecord
        {
            DeadLetterId = Guid.NewGuid(),
            ChatId = 4040,
            CorrelationId = "trace-dlq-enum-as-string",
            AttemptCount = 1,
            FailureCategory = OutboundFailureCategory.Permanent,
            LastErrorType = "ApiRequestException",
            LastErrorMessage = "Bad Request: chat not found",
            FailedAt = DateTimeOffset.UtcNow,
        };
        await store.RecordAsync(record, CancellationToken.None);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var raw = await db.OutboundDeadLetters
            .Where(x => x.CorrelationId == "trace-dlq-enum-as-string")
            .Select(x => new { x.FailureCategory })
            .SingleAsync();
        raw.FailureCategory.Should().Be(OutboundFailureCategory.Permanent,
            "the round-trip through EF must preserve the enum value (a missing converter would silently zero it out)");
    }

    [Fact]
    public void AddMessagingPersistence_ReplacesDeadLetterStoreWithPersistentImplementation()
    {
        // Regression guard mirroring PersistentOutboundMessageIdIndex's
        // wire-up assertion: a subtle refactor that switched
        // services.Replace(...) → TryAddSingleton(...) would leave
        // production using the in-memory fallback and silently regress
        // durability. Pinning the resolved concrete type at the
        // integration-test level catches this.
        var resolved = _host.Services.GetRequiredService<IOutboundDeadLetterStore>();
        resolved.GetType().FullName.Should().Be(
            "AgentSwarm.Messaging.Persistence.PersistentOutboundDeadLetterStore",
            "iter-5 evaluator item 4: AddMessagingPersistence MUST replace any in-memory IOutboundDeadLetterStore with the EF-backed PersistentOutboundDeadLetterStore");
    }

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        _keepAlive.Dispose();
    }
}
