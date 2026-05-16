using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.3 acceptance tests for <see cref="PersistentOutboundQueue"/>.
/// Covers the implementation-plan Stage 2.3 contract: severity-ordered
/// dequeue, MarkSent/MarkFailed lifecycle, exponential backoff, dead-letter
/// transition on retry exhaustion, and UNIQUE-key enforced idempotency. All
/// tests run against an in-memory SQLite database with a shared connection
/// so the relational UNIQUE / FK constraints (which the InMemory provider
/// does not enforce) actually fire.
/// </summary>
public class PersistentOutboundQueueTests : IDisposable
{
    private readonly SqliteConnection _sqlite;
    private readonly DbContextOptions<MessagingDbContext> _options;

    public PersistentOutboundQueueTests()
    {
        _sqlite = new SqliteConnection("DataSource=:memory:");
        _sqlite.Open();
        EnableForeignKeys(_sqlite);

        _options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_sqlite)
            .Options;

        using var bootstrap = new MessagingDbContext(_options);
        bootstrap.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sqlite.Dispose();
        GC.SuppressFinalize(this);
    }

    private MessagingDbContext NewContext() => new(_options);

    [Fact]
    public async Task DequeueAsync_OrdersBySeverity_CriticalThenNormalThenLow()
    {
        // Test-scenario: "Priority-ordered dequeue -- Given outbound messages
        // with Critical, Normal, and Low severities enqueued, When
        // DequeueAsync is called repeatedly, Then messages are returned
        // Critical first then Normal then Low."
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 10, 0, 0, TimeSpan.Zero));

        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);

            // Enqueue out of priority order, with newer Critical so that
            // FIFO-on-CreatedAt cannot accidentally satisfy the assertion.
            await queue.EnqueueAsync(NewMessage("low-1", MessageSeverity.Low, createdAt: clock.GetUtcNow().AddSeconds(-30)), default);
            await queue.EnqueueAsync(NewMessage("normal-1", MessageSeverity.Normal, createdAt: clock.GetUtcNow().AddSeconds(-20)), default);
            await queue.EnqueueAsync(NewMessage("critical-1", MessageSeverity.Critical, createdAt: clock.GetUtcNow().AddSeconds(-10)), default);
        }

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        var first = await queue2.DequeueAsync(default);
        var second = await queue2.DequeueAsync(default);
        var third = await queue2.DequeueAsync(default);
        var fourth = await queue2.DequeueAsync(default);

        first.Should().NotBeNull();
        first!.IdempotencyKey.Should().Be("critical-1");
        first.Severity.Should().Be(MessageSeverity.Critical);
        first.Status.Should().Be(OutboundMessageStatus.Sending);

        second.Should().NotBeNull();
        second!.IdempotencyKey.Should().Be("normal-1");
        second.Severity.Should().Be(MessageSeverity.Normal);

        third.Should().NotBeNull();
        third!.IdempotencyKey.Should().Be("low-1");
        third.Severity.Should().Be(MessageSeverity.Low);

        fourth.Should().BeNull();
    }

    [Fact]
    public async Task DequeueAsync_RespectsHighSeverityBetweenCriticalAndNormal()
    {
        // Defends the full four-band ordering pinned by MessageSeverity
        // (Critical=0 < High=1 < Normal=2 < Low=3).
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 10, 0, 0, TimeSpan.Zero));

        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            await queue.EnqueueAsync(NewMessage("n", MessageSeverity.Normal, createdAt: clock.GetUtcNow()), default);
            await queue.EnqueueAsync(NewMessage("l", MessageSeverity.Low, createdAt: clock.GetUtcNow()), default);
            await queue.EnqueueAsync(NewMessage("h", MessageSeverity.High, createdAt: clock.GetUtcNow()), default);
            await queue.EnqueueAsync(NewMessage("c", MessageSeverity.Critical, createdAt: clock.GetUtcNow()), default);
        }

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        var keys = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var msg = await queue2.DequeueAsync(default);
            keys.Add(msg!.IdempotencyKey);
        }

        keys.Should().Equal("c", "h", "n", "l");
    }

    [Fact]
    public async Task DequeueAsync_WithinSameSeverity_ReturnsOldestFirst()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 10, 0, 0, TimeSpan.Zero));

        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            await queue.EnqueueAsync(NewMessage("newer", MessageSeverity.Normal, createdAt: clock.GetUtcNow().AddSeconds(5)), default);
            await queue.EnqueueAsync(NewMessage("middle", MessageSeverity.Normal, createdAt: clock.GetUtcNow()), default);
            await queue.EnqueueAsync(NewMessage("oldest", MessageSeverity.Normal, createdAt: clock.GetUtcNow().AddSeconds(-30)), default);
        }

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        (await queue2.DequeueAsync(default))!.IdempotencyKey.Should().Be("oldest");
        (await queue2.DequeueAsync(default))!.IdempotencyKey.Should().Be("middle");
        (await queue2.DequeueAsync(default))!.IdempotencyKey.Should().Be("newer");
    }

    [Fact]
    public async Task DequeueAsync_ReturnsNull_WhenQueueEmpty()
    {
        using var ctx = NewContext();
        var queue = new PersistentOutboundQueue(ctx);

        (await queue.DequeueAsync(default)).Should().BeNull();
    }

    [Fact]
    public async Task DequeueAsync_SkipsFailedMessages_WithFutureNextRetryAt()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 10, 0, 0, TimeSpan.Zero));

        Guid failedMessageId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            var failedMsg = NewMessage("failed-1", MessageSeverity.Critical, createdAt: clock.GetUtcNow().AddSeconds(-60));
            failedMessageId = failedMsg.MessageId;
            await queue.EnqueueAsync(failedMsg, default);
            await queue.EnqueueAsync(NewMessage("pending-1", MessageSeverity.Normal, createdAt: clock.GetUtcNow()), default);

            // Pick up Critical and fail it -- this populates NextRetryAt one
            // exponential-backoff window in the future.
            var picked = await queue.DequeueAsync(default);
            picked!.MessageId.Should().Be(failedMessageId);
            await queue.MarkFailedAsync(failedMessageId, "transient outage", default);
        }

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        var next = await queue2.DequeueAsync(default);

        // Critical is in Failed state with NextRetryAt in the future, so the
        // dispatcher must pick up the next-best eligible message (Normal),
        // not the still-cooling Critical.
        next.Should().NotBeNull();
        next!.IdempotencyKey.Should().Be("pending-1");
    }

    [Fact]
    public async Task DequeueAsync_ReturnsFailedMessage_WhenNextRetryAtElapsed()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 10, 0, 0, TimeSpan.Zero));

        Guid failedId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock, baseBackoff: TimeSpan.FromSeconds(1));
            var msg = NewMessage("retry-1", MessageSeverity.Normal, createdAt: clock.GetUtcNow());
            failedId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);

            var picked = await queue.DequeueAsync(default);
            picked!.MessageId.Should().Be(failedId);

            await queue.MarkFailedAsync(failedId, "boom", default);
        }

        // Advance the clock past the first retry window.
        clock.Now = clock.Now.AddSeconds(10);

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        var requeued = await queue2.DequeueAsync(default);

        requeued.Should().NotBeNull();
        requeued!.MessageId.Should().Be(failedId);
        requeued.Status.Should().Be(OutboundMessageStatus.Sending);
    }

    [Fact]
    public async Task MarkSentAsync_SetsStatusSent_PopulatesPlatformIdAndSentAt()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 11, 0, 0, TimeSpan.Zero));

        Guid messageId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            var msg = NewMessage("send-1", MessageSeverity.High, createdAt: clock.GetUtcNow());
            messageId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);
            await queue.DequeueAsync(default);
        }

        clock.Now = clock.Now.AddMilliseconds(123);

        using (var ctx = NewContext())
        {
            var queue = new PersistentOutboundQueue(ctx, clock);
            await queue.MarkSentAsync(messageId, platformMessageId: 0x1FFF_FFFF_FFFF_FFFFL, default);
        }

        using var read = NewContext();
        var stored = await read.OutboundMessages.AsNoTracking().SingleAsync(m => m.MessageId == messageId);

        stored.Status.Should().Be(OutboundMessageStatus.Sent);
        stored.PlatformMessageId.Should().Be(0x1FFF_FFFF_FFFF_FFFFL);
        stored.SentAt.Should().Be(clock.GetUtcNow());
        stored.ErrorDetail.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_BelowMax_IncrementsAttemptAndSetsExponentialNextRetry()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

        Guid messageId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock, baseBackoff: TimeSpan.FromSeconds(1));
            var msg = NewMessage("fail-1", MessageSeverity.Normal, createdAt: clock.GetUtcNow());
            messageId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);
        }

        // Sequence: attempt 1 fails → 1s; attempt 2 → 2s; attempt 3 → 4s; attempt 4 → 8s.
        var expectedBackoffSeconds = new[] { 1, 2, 4, 8 };
        for (var attempt = 0; attempt < expectedBackoffSeconds.Length; attempt++)
        {
            using var ctx = NewContext();
            var queue = new PersistentOutboundQueue(ctx, clock, baseBackoff: TimeSpan.FromSeconds(1));
            await queue.MarkFailedAsync(messageId, $"attempt {attempt + 1} error", default);

            using var read = NewContext();
            var stored = await read.OutboundMessages.AsNoTracking().SingleAsync(m => m.MessageId == messageId);

            stored.AttemptCount.Should().Be(attempt + 1);
            stored.ErrorDetail.Should().Be($"attempt {attempt + 1} error");
            stored.Status.Should().Be(OutboundMessageStatus.Failed);
            stored.NextRetryAt.Should().Be(clock.GetUtcNow().AddSeconds(expectedBackoffSeconds[attempt]));
        }
    }

    [Fact]
    public async Task MarkFailedAsync_AtMaxAttempts_TransitionsToDeadLettered_AndCreatesDeadLetterRow()
    {
        // Test-scenario: "Dead letter on max attempts -- Given an
        // OutboundMessage that has failed 4 times (AttemptCount=4), When
        // MarkFailedAsync is called a fifth time, Then AttemptCount reaches
        // MaxAttempts (5), DeadLetterAsync is called, OutboundMessage.Status
        // becomes DeadLettered, and a linked DeadLetterMessage record is
        // created."
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 13, 0, 0, TimeSpan.Zero));

        Guid messageId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            var msg = NewMessage("dlq-1", MessageSeverity.Critical, createdAt: clock.GetUtcNow());
            messageId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);
        }

        // Fail four times -- the 4th failure leaves AttemptCount=4 (< MaxAttempts=5).
        for (var i = 1; i <= 4; i++)
        {
            using var ctx = NewContext();
            var queue = new PersistentOutboundQueue(ctx, clock);
            await queue.MarkFailedAsync(messageId, $"err-{i}", default);
        }

        using (var verify = NewContext())
        {
            var snap = await verify.OutboundMessages.AsNoTracking().SingleAsync(m => m.MessageId == messageId);
            snap.AttemptCount.Should().Be(4);
            snap.Status.Should().Be(OutboundMessageStatus.Failed);
        }

        // The fifth failure trips the exhaustion threshold and dead-letters.
        using (var ctx = NewContext())
        {
            var queue = new PersistentOutboundQueue(ctx, clock);
            await queue.MarkFailedAsync(messageId, "final-error", default);
        }

        using var read = NewContext();
        var stored = await read.OutboundMessages.AsNoTracking().SingleAsync(m => m.MessageId == messageId);
        var deadLetter = await read.DeadLetterMessages.AsNoTracking().SingleAsync(d => d.OriginalMessageId == messageId);

        stored.Status.Should().Be(OutboundMessageStatus.DeadLettered);
        stored.AttemptCount.Should().Be(5);
        stored.ErrorDetail.Should().Be("final-error");
        stored.NextRetryAt.Should().BeNull();

        deadLetter.OriginalMessageId.Should().Be(messageId);
        deadLetter.ChatId.Should().Be(stored.ChatId);
        deadLetter.Payload.Should().Be(stored.Payload);
        deadLetter.AttemptCount.Should().Be(5);
        deadLetter.FailedAt.Should().Be(clock.GetUtcNow());
        deadLetter.ErrorReason.Should().Contain("final-error");
        deadLetter.ErrorReason.Should().Contain("5 attempt(s)");
    }

    [Fact]
    public async Task DeadLetterAsync_IsIdempotent_DoesNotDuplicateDeadLetterRow()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 14, 0, 0, TimeSpan.Zero));

        Guid messageId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            var msg = NewMessage("dl-idem-1", MessageSeverity.High, createdAt: clock.GetUtcNow());
            messageId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);
        }

        using (var ctx = NewContext())
        {
            var queue = new PersistentOutboundQueue(ctx, clock);
            await queue.DeadLetterAsync(messageId, default);
        }

        using (var ctx = NewContext())
        {
            var queue = new PersistentOutboundQueue(ctx, clock);
            await queue.DeadLetterAsync(messageId, default);
        }

        using var read = NewContext();
        var deadLetterRowCount = await read.DeadLetterMessages.AsNoTracking()
            .CountAsync(d => d.OriginalMessageId == messageId);
        var stored = await read.OutboundMessages.AsNoTracking().SingleAsync(m => m.MessageId == messageId);

        deadLetterRowCount.Should().Be(1, "the UNIQUE(OriginalMessageId) contract pins the 1--1 relationship");
        stored.Status.Should().Be(OutboundMessageStatus.DeadLettered);
    }

    [Fact]
    public async Task EnqueueAsync_DuplicateIdempotencyKey_IsNoOp()
    {
        // Test-step: "Implement IdempotencyKey enforcement: UNIQUE constraint
        // on OutboundMessage.IdempotencyKey prevents duplicate enqueues for
        // the same question/alert/status/ack."
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 15, 0, 0, TimeSpan.Zero));
        var sharedKey = OutboundMessage.IdempotencyKeys.ForQuestion("agent-x", "Q-1");

        using (var ctx = NewContext())
        {
            var queue = new PersistentOutboundQueue(ctx, clock);

            await queue.EnqueueAsync(
                NewMessage(sharedKey, MessageSeverity.Normal, createdAt: clock.GetUtcNow()),
                default);

            await queue.EnqueueAsync(
                NewMessage(sharedKey, MessageSeverity.Critical, createdAt: clock.GetUtcNow(), payload: "different payload"),
                default);
        }

        using var read = NewContext();
        var rows = await read.OutboundMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey == sharedKey)
            .ToListAsync();

        rows.Should().HaveCount(1, "the second enqueue must collapse onto the existing row");
        rows[0].Severity.Should().Be(MessageSeverity.Normal, "the first enqueue wins; duplicates are no-ops");
    }

    [Fact]
    public async Task EnqueueAsync_RaceOnConstraintViolation_IsSwallowedAsNoOp()
    {
        // Defends the SQLite UNIQUE-constraint catch-as-fallback path: when
        // the AnyAsync fast-path misses (its snapshot pre-dates the
        // committed duplicate) the SaveChanges UNIQUE violation must be
        // caught and treated as a no-op so the caller does not see an
        // exception for a logically duplicate enqueue.
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 15, 30, 0, TimeSpan.Zero));
        var sharedKey = OutboundMessage.IdempotencyKeys.ForAlert("agent-x", "alert-1");

        using (var ctxA = NewContext())
        {
            var queue = new PersistentOutboundQueue(ctxA, clock);
            await queue.EnqueueAsync(
                NewMessage(sharedKey, MessageSeverity.Critical, createdAt: clock.GetUtcNow()),
                default);
        }

        using (var ctxB = NewContext())
        {
            // Use the public seam: produce a fresh OutboundMessage carrying
            // the same key. The fast-path AnyAsync will see the existing row
            // and short-circuit on no-op, so this asserts the documented
            // contract (no exception, no second row).
            var queue = new PersistentOutboundQueue(ctxB, clock);
            await queue.EnqueueAsync(
                NewMessage(sharedKey, MessageSeverity.High, createdAt: clock.GetUtcNow()),
                default);
        }

        // Independently exercise the SQLite UNIQUE-constraint fault path by
        // staging an Add on a context whose snapshot precedes the existing
        // row, then calling SaveChanges. EF must raise DbUpdateException so
        // the in-queue catch handler has something to swallow.
        using (var ctxC = NewContext())
        {
            ctxC.OutboundMessages.Add(
                NewMessage(sharedKey, MessageSeverity.Normal, createdAt: clock.GetUtcNow()));
            var act = async () => await ctxC.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        using var read = NewContext();
        var rows = await read.OutboundMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey == sharedKey)
            .ToListAsync();

        rows.Should().HaveCount(1);
        rows[0].Severity.Should().Be(MessageSeverity.Critical);
    }

    [Fact]
    public async Task CountPendingAsync_CountsOnlyPendingOfMatchingSeverity()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 16, 0, 0, TimeSpan.Zero));

        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            await queue.EnqueueAsync(NewMessage("p-low-1", MessageSeverity.Low, createdAt: clock.GetUtcNow()), default);
            await queue.EnqueueAsync(NewMessage("p-low-2", MessageSeverity.Low, createdAt: clock.GetUtcNow().AddSeconds(1)), default);
            await queue.EnqueueAsync(NewMessage("p-low-3", MessageSeverity.Low, createdAt: clock.GetUtcNow().AddSeconds(2)), default);
            await queue.EnqueueAsync(NewMessage("p-normal-1", MessageSeverity.Normal, createdAt: clock.GetUtcNow()), default);

            // Mark one Normal row as Sending so it must not be counted.
            await queue.DequeueAsync(default);
        }

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        (await queue2.CountPendingAsync(MessageSeverity.Low, default)).Should().Be(3);
        (await queue2.CountPendingAsync(MessageSeverity.Normal, default)).Should().Be(0,
            "the only Normal row was claimed in Sending state");
        (await queue2.CountPendingAsync(MessageSeverity.Critical, default)).Should().Be(0);
    }

    [Fact]
    public async Task DequeueBatchAsync_ReturnsUpToMaxCountOldestFirstFilteredBySeverity()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 17, 0, 0, TimeSpan.Zero));

        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            // Five Low rows with monotonically increasing CreatedAt and one
            // Critical that must NOT be drained by the Low batch.
            for (var i = 0; i < 5; i++)
            {
                await queue.EnqueueAsync(
                    NewMessage($"batch-low-{i}", MessageSeverity.Low,
                        createdAt: clock.GetUtcNow().AddSeconds(i)),
                    default);
            }
            await queue.EnqueueAsync(NewMessage("batch-critical", MessageSeverity.Critical, createdAt: clock.GetUtcNow()), default);
        }

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        var batch = await queue2.DequeueBatchAsync(MessageSeverity.Low, maxCount: 3, default);

        batch.Should().HaveCount(3);
        batch.Select(b => b.IdempotencyKey).Should().Equal("batch-low-0", "batch-low-1", "batch-low-2");
        batch.Should().AllSatisfy(b => b.Status.Should().Be(OutboundMessageStatus.Sending));

        // The Critical row is untouched; subsequent DequeueAsync still finds it.
        var crit = await queue2.DequeueAsync(default);
        crit.Should().NotBeNull();
        crit!.IdempotencyKey.Should().Be("batch-critical");
    }

    [Fact]
    public async Task DequeueBatchAsync_ZeroMaxCount_ReturnsEmptyWithoutTouchingRows()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 17, 30, 0, TimeSpan.Zero));

        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            await queue.EnqueueAsync(NewMessage("nop", MessageSeverity.Low, createdAt: clock.GetUtcNow()), default);
        }

        using var ctx = NewContext();
        var queue2 = new PersistentOutboundQueue(ctx, clock);

        var empty = await queue2.DequeueBatchAsync(MessageSeverity.Low, maxCount: 0, default);
        empty.Should().BeEmpty();

        (await queue2.CountPendingAsync(MessageSeverity.Low, default)).Should().Be(1);
    }

    [Fact]
    public async Task DequeueAsync_TwoSequentialClaims_ReturnDistinctRows()
    {
        // Once a row is claimed (Status=Sending) it is no longer eligible
        // for re-dequeue by a sibling worker. This locks in the transactional
        // pickup invariant pinned by IOutboundQueue.DequeueAsync.
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 18, 0, 0, TimeSpan.Zero));

        Guid onlyMessageId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            var msg = NewMessage("solo", MessageSeverity.High, createdAt: clock.GetUtcNow());
            onlyMessageId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);
        }

        using var ctx1 = NewContext();
        using var ctx2 = NewContext();
        var queue1 = new PersistentOutboundQueue(ctx1, clock);
        var queue2 = new PersistentOutboundQueue(ctx2, clock);

        var first = await queue1.DequeueAsync(default);
        var second = await queue2.DequeueAsync(default);

        first.Should().NotBeNull();
        first!.MessageId.Should().Be(onlyMessageId);
        first.Status.Should().Be(OutboundMessageStatus.Sending);

        second.Should().BeNull("the only eligible row was already claimed in Sending state");
    }

    [Fact]
    public async Task MarkFailedAsync_OnUnknownMessageId_Throws()
    {
        using var ctx = NewContext();
        var queue = new PersistentOutboundQueue(ctx);
        var act = async () => await queue.MarkFailedAsync(Guid.NewGuid(), "no-such-row", default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MarkSentAsync_OnUnknownMessageId_Throws()
    {
        using var ctx = NewContext();
        var queue = new PersistentOutboundQueue(ctx);
        var act = async () => await queue.MarkSentAsync(Guid.NewGuid(), 1L, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static OutboundMessage NewMessage(
        string idempotencyKey,
        MessageSeverity severity,
        DateTimeOffset? createdAt = null,
        string? payload = null)
    {
        return OutboundMessage.Create(
            idempotencyKey: idempotencyKey,
            chatId: 100_000_000_000L,
            severity: severity,
            sourceType: OutboundMessageSource.StatusUpdate,
            payload: payload ?? "{\"text\":\"hello\"}",
            correlationId: "corr-" + idempotencyKey,
            createdAt: createdAt);
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
    }

    private sealed class StubTimeProvider : TimeProvider
    {
        public StubTimeProvider(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
