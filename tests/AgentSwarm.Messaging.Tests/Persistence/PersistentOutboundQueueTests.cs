using System.Collections.Concurrent;
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
    // Shared-cache mode lets multiple SqliteConnection instances point at
    // the same in-memory database, which is required for the concurrent-
    // claim tests below: SQLite serializes operations on a *single*
    // connection, so the CAS race window can only be exercised when each
    // worker context owns its own connection. The holder connection keeps
    // the shared in-memory database alive for the lifetime of the test
    // (SQLite drops the database when its last connection closes).
    private readonly string _connectionString;
    private readonly SqliteConnection _holder;
    private readonly DbContextOptions<MessagingDbContext> _options;

    public PersistentOutboundQueueTests()
    {
        var dbName = "outbox_" + Guid.NewGuid().ToString("N");
        _connectionString = $"DataSource=file:{dbName}?mode=memory&cache=shared&Foreign Keys=True";

        _holder = new SqliteConnection(_connectionString);
        _holder.Open();

        _options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        using var bootstrap = new MessagingDbContext(_options);
        bootstrap.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _holder.Dispose();
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
    public async Task EnqueueAsync_ManyConcurrentDuplicates_ExactlyOneRowPersists_NoExceptionEscapes()
    {
        // Stress-style coverage for the catch-as-fallback path in
        // EnqueueAsync: under real concurrency, two contexts whose AnyAsync
        // snapshot both predate the eventually-committed duplicate must each
        // see their SaveChanges either (a) succeed exactly once or (b) hit
        // the SQLite UNIQUE constraint and be swallowed by the queue's
        // catch handler. The race window is widened by running many workers
        // against many independent connections via shared-cache mode; with
        // the catch path correctly wired, every enqueue completes without
        // throwing and exactly one row ends up in the database.
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 15, 30, 0, TimeSpan.Zero));
        var sharedKey = OutboundMessage.IdempotencyKeys.ForAlert("agent-x", "alert-stress");

        const int Workers = 16;
        var barrier = new Barrier(Workers);
        var exceptions = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, Workers).Select(i => Task.Run(async () =>
        {
            try
            {
                using var ctx = NewContext();
                var queue = new PersistentOutboundQueue(ctx, clock);
                var msg = NewMessage(sharedKey, MessageSeverity.Normal,
                    createdAt: clock.GetUtcNow(), payload: $"p-{i}");
                barrier.SignalAndWait();
                await queue.EnqueueAsync(msg, default);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty(
            "the UNIQUE-violation catch handler in EnqueueAsync must swallow concurrent duplicates");

        using var read = NewContext();
        var rows = await read.OutboundMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey == sharedKey)
            .ToListAsync();

        rows.Should().HaveCount(1,
            "the UNIQUE(IdempotencyKey) constraint plus the catch handler collapses every duplicate to a single row");
    }

    [Fact]
    public async Task TryClaimAsync_LosingWorkerWithStaleSnapshot_ReturnsNullViaConditionalUpdate()
    {
        // Deterministically exercise the atomic CAS in TryClaimAsync:
        // worker A loads the candidate id, worker B loads the same candidate
        // id from an independent context (both snapshots see Status=Pending),
        // worker A's TryClaim transitions the row to Sending, then worker B's
        // TryClaim issues `UPDATE ... WHERE Status IN (Pending, Failed)`
        // which matches 0 rows because A already changed Status. B must
        // return null without claiming the row. This is the exact race the
        // iter-1 evaluator called out as the operational-risk hole.
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 19, 0, 0, TimeSpan.Zero));

        Guid contendedId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            var msg = NewMessage("cas-contended", MessageSeverity.Critical, createdAt: clock.GetUtcNow());
            contendedId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);
        }

        using var ctxA = NewContext();
        using var ctxB = NewContext();
        var queueA = new PersistentOutboundQueue(ctxA, clock);
        var queueB = new PersistentOutboundQueue(ctxB, clock);

        // Both workers have snapshots with Status=Pending at this point.
        // A claims first.
        var claimedByA = await queueA.TryClaimAsync(contendedId, clock.GetUtcNow(), default);
        // B's claim attempt runs against the now-Sending row.
        var claimedByB = await queueB.TryClaimAsync(contendedId, clock.GetUtcNow(), default);

        claimedByA.Should().NotBeNull("worker A's CAS hit Status=Pending and transitioned it");
        claimedByA!.MessageId.Should().Be(contendedId);
        claimedByA.Status.Should().Be(OutboundMessageStatus.Sending);

        claimedByB.Should().BeNull(
            "worker B's CAS must reject the stale claim because Status is no longer Pending/Failed-due");

        using var read = NewContext();
        var stored = await read.OutboundMessages.AsNoTracking().SingleAsync(m => m.MessageId == contendedId);
        stored.Status.Should().Be(OutboundMessageStatus.Sending,
            "the atomic UPDATE happened exactly once");
    }

    [Fact]
    public async Task DequeueAsync_ManyConcurrentWorkersSingleRow_AtomicCASElectsExactlyOneWinner()
    {
        // End-to-end stress complement to the deterministic CAS test:
        // N workers, each with its own context and SQLite connection,
        // race to claim the single eligible row. The atomic conditional
        // UPDATE inside TryClaimAsync guarantees exactly one worker
        // receives a non-null result; all others return null.
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 19, 30, 0, TimeSpan.Zero));

        Guid soloId;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            var msg = NewMessage("solo-race", MessageSeverity.Critical, createdAt: clock.GetUtcNow());
            soloId = msg.MessageId;
            await queue.EnqueueAsync(msg, default);
        }

        const int Workers = 8;
        var barrier = new Barrier(Workers);

        var tasks = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
        {
            using var ctx = NewContext();
            var queue = new PersistentOutboundQueue(ctx, clock);
            barrier.SignalAndWait();
            return await queue.DequeueAsync(default);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        var winners = results.Where(r => r is not null).ToList();

        winners.Should().HaveCount(1, "atomic CAS must elect exactly one winner");
        winners[0]!.MessageId.Should().Be(soloId);
        winners[0]!.Status.Should().Be(OutboundMessageStatus.Sending);

        using var read = NewContext();
        var stored = await read.OutboundMessages.AsNoTracking().SingleAsync(m => m.MessageId == soloId);
        stored.Status.Should().Be(OutboundMessageStatus.Sending);
    }

    [Fact]
    public async Task DequeueAsync_ManyConcurrentWorkersManyRows_EachRowClaimedByExactlyOneWorker()
    {
        // N workers race over M rows. Each row must be claimed by exactly
        // one worker — total winners count equals row count, and no two
        // workers return the same MessageId.
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 20, 0, 0, TimeSpan.Zero));

        const int Rows = 20;
        var rowIds = new HashSet<Guid>();
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            for (var i = 0; i < Rows; i++)
            {
                var msg = NewMessage($"multi-race-{i}", MessageSeverity.Normal,
                    createdAt: clock.GetUtcNow().AddMilliseconds(i));
                rowIds.Add(msg.MessageId);
                await queue.EnqueueAsync(msg, default);
            }
        }

        const int Workers = 8;
        var barrier = new Barrier(Workers);
        var claimsPerWorker = new ConcurrentBag<List<Guid>>();

        var tasks = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
        {
            using var ctx = NewContext();
            var queue = new PersistentOutboundQueue(ctx, clock);
            var mine = new List<Guid>();
            barrier.SignalAndWait();
            while (true)
            {
                var claimed = await queue.DequeueAsync(default);
                if (claimed is null) break;
                mine.Add(claimed.MessageId);
            }
            claimsPerWorker.Add(mine);
        })).ToArray();

        await Task.WhenAll(tasks);

        var allClaims = claimsPerWorker.SelectMany(x => x).ToList();
        allClaims.Should().HaveCount(Rows,
            "every row must be claimed exactly once across all workers");
        allClaims.Distinct().Should().HaveCount(Rows,
            "no row may be claimed by more than one worker (atomic CAS invariant)");
        allClaims.Should().BeEquivalentTo(rowIds);
    }

    [Fact]
    public async Task DequeueBatchAsync_TwoConcurrentBatchClaims_NoRowReturnedByBothWorkers()
    {
        // Same invariant for the batch dequeue surface used by the low-
        // priority status-update dispatcher (architecture.md Section 10.4):
        // overlapping candidate snapshots resolve to disjoint claim sets.
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 5, 16, 20, 30, 0, TimeSpan.Zero));

        const int Rows = 12;
        using (var seed = NewContext())
        {
            var queue = new PersistentOutboundQueue(seed, clock);
            for (var i = 0; i < Rows; i++)
            {
                await queue.EnqueueAsync(
                    NewMessage($"batch-race-{i}", MessageSeverity.Low,
                        createdAt: clock.GetUtcNow().AddMilliseconds(i)),
                    default);
            }
        }

        const int Workers = 4;
        var barrier = new Barrier(Workers);

        var tasks = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
        {
            using var ctx = NewContext();
            var queue = new PersistentOutboundQueue(ctx, clock);
            barrier.SignalAndWait();
            return (IReadOnlyList<OutboundMessage>)await queue.DequeueBatchAsync(
                MessageSeverity.Low, maxCount: Rows, default);
        })).ToArray();

        var allBatches = await Task.WhenAll(tasks);
        var allIds = allBatches.SelectMany(b => b).Select(m => m.MessageId).ToList();

        allIds.Should().HaveCount(Rows,
            "every eligible row must end up in exactly one batch");
        allIds.Distinct().Should().HaveCount(Rows,
            "no row may appear in two batches (atomic per-row CAS invariant)");
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

    private sealed class StubTimeProvider : TimeProvider
    {
        public StubTimeProvider(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
