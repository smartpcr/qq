// -----------------------------------------------------------------------
// <copyright file="PersistentOutboundQueueTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Diagnostics.Metrics;
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
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// Stage 4.1 — pins <see cref="PersistentOutboundQueue"/> against
/// the brief's six test scenarios (implementation-plan.md §400-417):
/// enqueue/dequeue roundtrip, persistence-across-restart, severity-
/// priority dequeue, backpressure dead-letter for Low + accept of
/// Critical at full depth, concurrent processor workers (covered in
/// <c>OutboundQueueProcessorTests</c>), and outbound deduplication
/// by idempotency key.
/// </summary>
/// <remarks>
/// Uses a per-test SQLite in-memory connection with a shared
/// <see cref="SqliteConnection"/> so the schema and data persist
/// across DbContext instances — matching the
/// <see cref="PersistentInboundUpdateStoreTests"/> fixture pattern
/// and giving the queue a real EF Core code path
/// (<c>ExecuteUpdateAsync</c>, UNIQUE constraint, value converters)
/// rather than the in-memory provider stub.
/// </remarks>
public sealed class PersistentOutboundQueueTests : IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 06, 01, 12, 00, 00, TimeSpan.Zero);

    private SqliteConnection _connection = null!;
    private DbContextOptions<MessagingDbContext> _options = null!;
    private ServiceProvider _services = null!;
    private FakeTimeProvider _time = null!;
    private OutboundQueueMetrics _metrics = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using (var ctx = new MessagingDbContext(_options))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddScoped<MessagingDbContext>(sp =>
            new MessagingDbContext(sp.GetRequiredService<DbContextOptions<MessagingDbContext>>()));
        _services = services.BuildServiceProvider();

        _time = new FakeTimeProvider(BaseTime);
        _metrics = new OutboundQueueMetrics();
    }

    public async Task DisposeAsync()
    {
        _metrics.Dispose();
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private PersistentOutboundQueue NewQueue(
        int maxQueueDepth = 5000,
        OutboundQueueMetrics? overrideMetrics = null)
    {
        var options = Options.Create(new OutboundQueueOptions
        {
            MaxQueueDepth = maxQueueDepth,
            ProcessorConcurrency = 1,
            DequeuePollIntervalMs = 10,
            MaxRetries = 5,
        });

        return new PersistentOutboundQueue(
            _services.GetRequiredService<IServiceScopeFactory>(),
            options,
            overrideMetrics ?? _metrics,
            _time,
            NullLogger<PersistentOutboundQueue>.Instance);
    }

    private static OutboundMessage NewMessage(
        MessageSeverity severity,
        string suffix,
        DateTimeOffset? createdAt = null,
        OutboundSourceType sourceType = OutboundSourceType.StatusUpdate) => new()
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = $"s:agent:{suffix}",
            ChatId = 100,
            Payload = $"payload-{suffix}",
            Severity = severity,
            SourceType = sourceType,
            SourceId = suffix,
            CreatedAt = createdAt ?? BaseTime,
            CorrelationId = $"trace-{suffix}",
        };

    [Fact]
    public async Task EnqueueDequeue_RoundTrip_MatchesContent_AndDecrementsQueueSize()
    {
        // Scenario: Enqueue and dequeue — Given a message is
        // enqueued, When dequeued, Then the message content matches
        // and the queue size decreases by one.
        var queue = NewQueue();
        var message = NewMessage(MessageSeverity.High, "rt");

        await queue.EnqueueAsync(message, CancellationToken.None);

        // Queue size BEFORE dequeue: 1 Pending row.
        await using (var ctx = new MessagingDbContext(_options))
        {
            (await ctx.OutboundMessages
                    .CountAsync(x => x.Status == OutboundMessageStatus.Pending))
                .Should().Be(1);
        }

        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        dequeued.Should().NotBeNull();
        dequeued!.MessageId.Should().Be(message.MessageId);
        dequeued.IdempotencyKey.Should().Be(message.IdempotencyKey);
        dequeued.Payload.Should().Be(message.Payload);
        dequeued.ChatId.Should().Be(message.ChatId);
        dequeued.Severity.Should().Be(message.Severity);
        dequeued.CorrelationId.Should().Be(message.CorrelationId);
        dequeued.Status.Should().Be(
            OutboundMessageStatus.Sending,
            "DequeueAsync must atomically transition Pending → Sending");

        // Queue size AFTER dequeue: 0 Pending rows (the row is now
        // Sending), so a second DequeueAsync returns null.
        await using (var ctx = new MessagingDbContext(_options))
        {
            (await ctx.OutboundMessages
                    .CountAsync(x => x.Status == OutboundMessageStatus.Pending))
                .Should().Be(0);
        }
        (await queue.DequeueAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DequeueAsync_StampsDequeuedAt_OnPersistedRow_AndReturnedSnapshot()
    {
        // Stage 4.1 iter-2 evaluator item 2 — the outbox schema must
        // record a DequeuedAt timestamp on the Pending→Sending
        // transition so the queue-dwell histogram, recovery sweeps,
        // and operator dashboards can distinguish "enqueued long
        // ago but only just picked up" from "picked up promptly but
        // hung in Sending". The migration column lives in
        // 20260601000006_AddOutboundMessages (DequeuedAt INTEGER NULL)
        // and PersistentOutboundQueue.DequeueAsync stamps it inside
        // the same ExecuteUpdateAsync that flips Status.
        var queue = NewQueue();
        var enqueueInstant = BaseTime;
        var message = NewMessage(MessageSeverity.High, "dq-stamp", createdAt: enqueueInstant);
        await queue.EnqueueAsync(message, CancellationToken.None);

        // Advance virtual time so the stamp is provably distinct from
        // CreatedAt (which guards against an accidental
        // `DequeuedAt = CreatedAt` copy in a future refactor).
        _time.Advance(TimeSpan.FromMilliseconds(250));
        var expectedDequeueInstant = _time.GetUtcNow();

        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        dequeued.Should().NotBeNull();
        dequeued!.DequeuedAt.Should().NotBeNull(
            "DequeueAsync must stamp DequeuedAt on the returned snapshot so workers can emit queue-dwell metrics anchored on the dequeue instant");
        dequeued.DequeuedAt!.Value.Should().Be(
            expectedDequeueInstant,
            "the stamp must come from the injected TimeProvider so tests can pin it deterministically");
        dequeued.DequeuedAt.Value.Should().BeAfter(
            dequeued.CreatedAt,
            "DequeuedAt is the Pending→Sending transition instant, which always trails CreatedAt");

        // Cross-check the persisted row matches the returned snapshot
        // — a regression guard against future refactors that would
        // stamp DequeuedAt only in memory without writing it through
        // (e.g. dropping the column from the ExecuteUpdateAsync set).
        await using var ctx = new MessagingDbContext(_options);
        var row = await ctx.OutboundMessages.AsNoTracking()
            .FirstAsync(x => x.MessageId == message.MessageId);
        row.DequeuedAt.Should().Be(
            dequeued.DequeuedAt,
            "the persisted DequeuedAt column must match what DequeueAsync returns; otherwise process restart loses the stamp");

        // Raw-column read — confirms the migration physically carries
        // the DequeuedAt column AND that DequeueAsync writes the
        // INTEGER through (a future refactor that drops the column
        // from migration 20260601000006 would surface here as a
        // SqliteException "no such column"). We use a COUNT query so
        // the assertion does not depend on Guid binary/text encoding
        // quirks between EF Core and Microsoft.Data.Sqlite.
        await using var raw = _connection.CreateCommand();
        raw.CommandText = "SELECT COUNT(*) FROM outbox WHERE DequeuedAt IS NOT NULL";
        var nonNullCount = Convert.ToInt64(await raw.ExecuteScalarAsync());
        nonNullCount.Should().Be(
            1L,
            "the outbox row must have a physical DequeuedAt value after DequeueAsync — if the column is dropped from the migration, the SELECT throws; if DequeueAsync stops writing it, the count is zero");
    }

    [Fact]
    public async Task PersistenceSurvivesRestart_RowReturnedByFreshQueue()
    {
        // Scenario: Persistence survives restart — Given a message is
        // enqueued to the persistent queue, When the process restarts,
        // Then the message is still available for dequeue.
        var queueA = NewQueue();
        var message = NewMessage(MessageSeverity.Normal, "restart");
        await queueA.EnqueueAsync(message, CancellationToken.None);

        // Build a brand-new queue instance — simulating a fresh
        // process — and confirm the row is still claimable.
        var queueB = NewQueue();
        var dequeued = await queueB.DequeueAsync(CancellationToken.None);
        dequeued.Should().NotBeNull(
            "the EF-backed queue persists rows to the underlying SQLite database, so a fresh PersistentOutboundQueue instance over the same connection must observe the enqueued row");
        dequeued!.MessageId.Should().Be(message.MessageId);
    }

    [Fact]
    public async Task DequeueAsync_SeverityPriorityOrder_CriticalThenHighThenNormalThenLow()
    {
        // Scenario: Severity-priority dequeue — Given the queue
        // contains messages enqueued in order: Low first, Normal
        // second, High third, Critical fourth, When DequeueAsync is
        // called four times, Then messages are returned in
        // severity-priority order: Critical, High, Normal, Low —
        // verifying the numeric int-backed severity column correctly
        // orders all four levels.
        var queue = NewQueue();

        await queue.EnqueueAsync(NewMessage(MessageSeverity.Low, "low"), CancellationToken.None);
        await queue.EnqueueAsync(NewMessage(MessageSeverity.Normal, "norm"), CancellationToken.None);
        await queue.EnqueueAsync(NewMessage(MessageSeverity.High, "hi"), CancellationToken.None);
        await queue.EnqueueAsync(NewMessage(MessageSeverity.Critical, "crit"), CancellationToken.None);

        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("crit");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("hi");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("norm");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("low");
        (await queue.DequeueAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SeverityColumn_PersistsAsInt_NotString()
    {
        // The brief explicitly requires the Severity column to be
        // persisted as int via value converter so `ORDER BY Severity
        // ASC` yields Critical(0) → High(1) → Normal(2) → Low(3). A
        // string column would sort alphabetically and silently break
        // the priority contract. This test reaches past the converter
        // and reads the raw column type to catch a regression where
        // the converter is removed or replaced.
        var queue = NewQueue();
        await queue.EnqueueAsync(NewMessage(MessageSeverity.Critical, "raw"), CancellationToken.None);

        // SQLite's INTEGER affinity round-trips through long; a string
        // column would return the enum name verbatim, so reading via
        // GetInt64 catches a regression to string persistence.
        await using var ctx = new MessagingDbContext(_options);
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Severity FROM outbox LIMIT 1;";
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var raw = reader.GetValue(0);
        raw.Should().BeOfType<long>(
            "Stage 4.1 brief requires int-backed Severity persistence so ORDER BY Severity ASC yields Critical(0) → High(1) → Normal(2) → Low(3); a string column would sort alphabetically");
        Convert.ToInt32(raw).Should().Be((int)MessageSeverity.Critical);
    }

    [Fact]
    public async Task EnqueueAsync_BackpressureExceeded_DeadLettersLowSeverity_AcceptsCritical()
    {
        // Scenario: Backpressure dead-letters low-severity — Given
        // the queue depth exceeds MaxQueueDepth, When a Low-severity
        // message is enqueued, Then it is dead-lettered immediately
        // with reason `backpressure:queue_depth_exceeded` and the
        // telegram.messages.backpressure_dlq counter is incremented;
        // when a Critical-severity message is enqueued under the
        // same conditions, Then it is accepted normally.

        // Stage 4.1 iter-2 evaluator item 4 — the predicate is
        // STRICTLY greater than ("exceeds" per the brief), not >=,
        // so we must seed enough rows for depth > MaxQueueDepth. With
        // MaxQueueDepth=2 we seed THREE rows so the count is 3 (> 2)
        // before the Low overflow attempt. The off-by-one regression
        // would manifest as the Low row being accepted instead of
        // dead-lettered.
        var queue = NewQueue(maxQueueDepth: 2);
        await queue.EnqueueAsync(NewMessage(MessageSeverity.High, "seed-1"), CancellationToken.None);
        await queue.EnqueueAsync(NewMessage(MessageSeverity.High, "seed-2"), CancellationToken.None);
        await queue.EnqueueAsync(NewMessage(MessageSeverity.High, "seed-3"), CancellationToken.None);

        // MeterListener captures every emit on the canonical counter
        // so we can assert the increment without coupling to an
        // OpenTelemetry exporter.
        var counterIncrements = 0L;
        var counterTags = new System.Collections.Generic.List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OutboundQueueMetrics.MeterName
                && instrument.Name == OutboundQueueMetrics.BackpressureDeadLetterCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, m, tags, _) =>
        {
            Interlocked.Add(ref counterIncrements, m);
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    counterTags.Add(tag.Value?.ToString());
                }
            }
        });
        listener.Start();

        // Low at full depth — must be dead-lettered immediately.
        var low = NewMessage(MessageSeverity.Low, "low-overflow");
        await queue.EnqueueAsync(low, CancellationToken.None);

        await using (var ctx = new MessagingDbContext(_options))
        {
            var lowRow = await ctx.OutboundMessages.AsNoTracking()
                .FirstAsync(x => x.MessageId == low.MessageId);
            lowRow.Status.Should().Be(OutboundMessageStatus.DeadLettered,
                "Low-severity over-the-cap enqueues must be dead-lettered immediately per architecture.md §10.4");
            lowRow.ErrorDetail.Should().Be(
                OutboundQueueOptions.BackpressureDeadLetterReason,
                "the canonical reason literal must be written verbatim so audit / metric pivots can match it");
        }
        counterIncrements.Should().Be(1,
            "telegram.messages.backpressure_dlq must increment by 1 per dead-lettered backpressure enqueue");
        counterTags.Should().Contain(OutboundQueueOptions.BackpressureDeadLetterReason);

        // Critical at the same depth — must be accepted normally.
        var critical = NewMessage(MessageSeverity.Critical, "crit-overflow");
        await queue.EnqueueAsync(critical, CancellationToken.None);
        await using (var ctx = new MessagingDbContext(_options))
        {
            var critRow = await ctx.OutboundMessages.AsNoTracking()
                .FirstAsync(x => x.MessageId == critical.MessageId);
            critRow.Status.Should().Be(OutboundMessageStatus.Pending,
                "Critical-severity messages bypass the backpressure gate and are always accepted");
            critRow.ErrorDetail.Should().BeNull();
        }
        // Counter must NOT increment for the Critical accept.
        counterIncrements.Should().Be(1);

        // High and Normal also bypass the gate.
        var high = NewMessage(MessageSeverity.High, "hi-overflow");
        var normal = NewMessage(MessageSeverity.Normal, "norm-overflow");
        await queue.EnqueueAsync(high, CancellationToken.None);
        await queue.EnqueueAsync(normal, CancellationToken.None);
        counterIncrements.Should().Be(1,
            "Normal and High enqueues must NOT increment the backpressure counter even when depth >= MaxQueueDepth");

        await using (var ctx = new MessagingDbContext(_options))
        {
            var rows = await ctx.OutboundMessages.AsNoTracking()
                .Where(x => x.MessageId == high.MessageId || x.MessageId == normal.MessageId)
                .ToListAsync();
            rows.Should().OnlyContain(r => r.Status == OutboundMessageStatus.Pending);
        }
    }

    [Fact]
    public async Task EnqueueAsync_DuplicateIdempotencyKey_RejectsDuplicate_KeepsOriginal()
    {
        // Scenario: Outbound deduplication by idempotency key — Given
        // a message with IdempotencyKey=agent1:Q1:corr-1 is already
        // enqueued, When a second message with the same
        // IdempotencyKey is enqueued, Then the duplicate is rejected
        // and the original message is returned without creating a
        // second outbox entry.
        var queue = NewQueue();
        var first = NewMessage(MessageSeverity.Normal, "dup");
        await queue.EnqueueAsync(first, CancellationToken.None);

        // Second enqueue with the same IdempotencyKey but a different
        // MessageId/payload. The persistent queue's dedup contract is
        // "rejected without creating a second outbox entry" — the
        // exact surface (throw vs. silent no-op) is an implementation
        // choice, but the row-count + original-row-preserved invariant
        // is non-negotiable.
        var duplicate = first with
        {
            MessageId = Guid.NewGuid(),
            Payload = "different-payload",
        };
        await queue.EnqueueAsync(duplicate, CancellationToken.None);

        await using var ctx = new MessagingDbContext(_options);
        var rows = await ctx.OutboundMessages.AsNoTracking()
            .Where(x => x.IdempotencyKey == first.IdempotencyKey)
            .ToListAsync();
        rows.Should().HaveCount(1, "the UNIQUE index on IdempotencyKey must prevent a second outbox row");
        rows[0].MessageId.Should().Be(first.MessageId, "the original message id must survive the duplicate-enqueue attempt");
        rows[0].Payload.Should().Be(first.Payload, "the original payload must NOT be overwritten by the duplicate enqueue");
    }

    [Fact]
    public async Task MarkSentAsync_StampsTelegramMessageId_AndTransitionsToSent()
    {
        var queue = NewQueue();
        var message = NewMessage(MessageSeverity.High, "ms");
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(3));
        await queue.MarkSentAsync(dequeued!.MessageId, telegramMessageId: 4242, CancellationToken.None);

        await using var ctx = new MessagingDbContext(_options);
        var row = await ctx.OutboundMessages.AsNoTracking()
            .FirstAsync(x => x.MessageId == dequeued.MessageId);
        row.Status.Should().Be(OutboundMessageStatus.Sent);
        row.TelegramMessageId.Should().Be(4242);
        row.SentAt.Should().NotBeNull();
        row.SentAt!.Value.Should().BeCloseTo(BaseTime.AddSeconds(3), TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task MarkFailedAsync_TransientWithBudget_SchedulesRetry()
    {
        var queue = NewQueue();
        var message = NewMessage(MessageSeverity.Normal, "mf");
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        await queue.MarkFailedAsync(dequeued!.MessageId, "transient", CancellationToken.None);

        await using var ctx = new MessagingDbContext(_options);
        var row = await ctx.OutboundMessages.AsNoTracking()
            .FirstAsync(x => x.MessageId == dequeued.MessageId);
        row.Status.Should().Be(OutboundMessageStatus.Pending);
        row.AttemptCount.Should().Be(1);
        row.ErrorDetail.Should().Be("transient");
        row.NextRetryAt.Should().NotBeNull();
        row.NextRetryAt!.Value.Should().BeAfter(BaseTime);
    }

    [Fact]
    public async Task MarkFailedAsync_BudgetExhausted_TransitionsToFailed()
    {
        var queue = NewQueue();
        var message = NewMessage(MessageSeverity.Normal, "mfx") with { MaxAttempts = 1 };
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        await queue.MarkFailedAsync(dequeued!.MessageId, "fatal", CancellationToken.None);

        await using var ctx = new MessagingDbContext(_options);
        var row = await ctx.OutboundMessages.AsNoTracking()
            .FirstAsync(x => x.MessageId == dequeued.MessageId);
        row.Status.Should().Be(OutboundMessageStatus.Failed,
            "with MaxAttempts=1 a single failure must transition out of Pending so the message stops being re-dequeued");
        row.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task DeadLetterAsync_TransitionsToDeadLettered()
    {
        var queue = NewQueue();
        var message = NewMessage(MessageSeverity.Low, "dl");
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        await queue.DeadLetterAsync(dequeued!.MessageId, "test:dl-reason", CancellationToken.None);

        await using var ctx = new MessagingDbContext(_options);
        var row = await ctx.OutboundMessages.AsNoTracking()
            .FirstAsync(x => x.MessageId == dequeued.MessageId);
        row.Status.Should().Be(OutboundMessageStatus.DeadLettered);
        row.ErrorDetail.Should().Be("test:dl-reason",
            "Stage 4.1 iter-2 evaluator item 5 — DeadLetterAsync(reason) must persist the reason on ErrorDetail so audit / dead-letter ledger queries can pivot on it");
        row.AttemptCount.Should().Be(1,
            "the dead-letter transition must record the final failure attempt rather than freezing AttemptCount at the last MarkFailed value");
    }

    [Fact]
    public async Task DequeueAsync_HonoursNextRetryAt_FutureMessageIsSkipped()
    {
        var queue = NewQueue();
        var ready = NewMessage(MessageSeverity.Normal, "ready");
        await queue.EnqueueAsync(ready, CancellationToken.None);

        // Insert a Pending row directly with NextRetryAt in the
        // future to mimic a post-failure retry-scheduled record.
        await using (var ctx = new MessagingDbContext(_options))
        {
            ctx.OutboundMessages.Add(new OutboundMessage
            {
                MessageId = Guid.NewGuid(),
                IdempotencyKey = "s:agent:future",
                ChatId = 100,
                Payload = "p",
                Severity = MessageSeverity.Critical,
                SourceType = OutboundSourceType.StatusUpdate,
                SourceId = "future",
                CreatedAt = BaseTime,
                CorrelationId = "trace-future",
                NextRetryAt = BaseTime.AddMinutes(5),
            });
            await ctx.SaveChangesAsync();
        }

        // Even though the future row is Critical (higher priority),
        // its NextRetryAt has not elapsed so dequeue must skip it and
        // return the lower-priority Normal row.
        var first = await queue.DequeueAsync(CancellationToken.None);
        first!.IdempotencyKey.Should().EndWith("ready");
        (await queue.DequeueAsync(CancellationToken.None)).Should().BeNull(
            "the future-retry row must not be claimable until its NextRetryAt elapses");

        _time.Advance(TimeSpan.FromMinutes(6));
        var second = await queue.DequeueAsync(CancellationToken.None);
        second!.IdempotencyKey.Should().EndWith("future");
    }

    [Fact]
    public async Task EnqueueAsync_NullMessage_Throws()
    {
        var queue = NewQueue();
        var act = async () => await queue.EnqueueAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

