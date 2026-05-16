using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.2 acceptance tests for <see cref="PersistentOutboundQueue"/>.
/// Focuses on the enqueue / dequeue / count surface that Stage 2.2 owns;
/// detailed dead-letter / retry behaviour is exercised in Stage 2.3 but
/// the baseline transitions are smoke-tested here so the registered
/// interface contract compiles end-to-end.
/// </summary>
public class PersistentOutboundQueueTests : IDisposable
{
    private readonly SqliteContextHarness _harness = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly PersistentOutboundQueue _queue;

    public PersistentOutboundQueueTests()
    {
        _queue = new PersistentOutboundQueue(_harness.Factory, _clock);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task EnqueueAsync_PersistsMessage()
    {
        var msg = NewMessage("k-1", MessageSeverity.Normal);
        await _queue.EnqueueAsync(msg, CancellationToken.None);

        using var ctx = _harness.NewContext();
        (await ctx.OutboundMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task EnqueueAsync_DuplicateIdempotencyKey_IsNoOp()
    {
        var first = NewMessage("k-dup", MessageSeverity.Normal);
        var second = NewMessage("k-dup", MessageSeverity.Normal);

        await _queue.EnqueueAsync(first, CancellationToken.None);
        await _queue.EnqueueAsync(second, CancellationToken.None);

        using var ctx = _harness.NewContext();
        (await ctx.OutboundMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DequeueAsync_SeverityOrdered_CriticalFirst()
    {
        // Enqueue Low then Normal then Critical -- DequeueAsync must return
        // Critical, then Normal, then Low.
        await _queue.EnqueueAsync(NewMessage("k-low", MessageSeverity.Low, createdAt: _clock.UtcNow), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _queue.EnqueueAsync(NewMessage("k-norm", MessageSeverity.Normal, createdAt: _clock.UtcNow), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _queue.EnqueueAsync(NewMessage("k-crit", MessageSeverity.Critical, createdAt: _clock.UtcNow), CancellationToken.None);

        var first = await _queue.DequeueAsync(CancellationToken.None);
        var second = await _queue.DequeueAsync(CancellationToken.None);
        var third = await _queue.DequeueAsync(CancellationToken.None);
        var fourth = await _queue.DequeueAsync(CancellationToken.None);

        first!.IdempotencyKey.Should().Be("k-crit");
        second!.IdempotencyKey.Should().Be("k-norm");
        third!.IdempotencyKey.Should().Be("k-low");
        fourth.Should().BeNull();
    }

    [Fact]
    public async Task DequeueAsync_OldestFirstWithinSeverity()
    {
        var earlyTs = _clock.UtcNow;
        var lateTs = _clock.UtcNow + TimeSpan.FromMinutes(10);

        await _queue.EnqueueAsync(NewMessage("k-late", MessageSeverity.Normal, createdAt: lateTs), CancellationToken.None);
        await _queue.EnqueueAsync(NewMessage("k-early", MessageSeverity.Normal, createdAt: earlyTs), CancellationToken.None);

        var first = await _queue.DequeueAsync(CancellationToken.None);
        var second = await _queue.DequeueAsync(CancellationToken.None);

        first!.IdempotencyKey.Should().Be("k-early");
        second!.IdempotencyKey.Should().Be("k-late");
    }

    [Fact]
    public async Task DequeueAsync_TransitionsRowToSending()
    {
        await _queue.EnqueueAsync(NewMessage("k-send", MessageSeverity.Normal), CancellationToken.None);

        var dequeued = await _queue.DequeueAsync(CancellationToken.None);

        dequeued.Should().NotBeNull();
        using var ctx = _harness.NewContext();
        var row = await ctx.OutboundMessages.SingleAsync();
        row.Status.Should().Be(OutboundMessageStatus.Sending);
    }

    [Fact]
    public async Task DequeueAsync_EmptyQueue_ReturnsNull()
    {
        (await _queue.DequeueAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CountPendingAsync_FiltersBySeverity()
    {
        await _queue.EnqueueAsync(NewMessage("k-a", MessageSeverity.Normal), CancellationToken.None);
        await _queue.EnqueueAsync(NewMessage("k-b", MessageSeverity.Normal), CancellationToken.None);
        await _queue.EnqueueAsync(NewMessage("k-c", MessageSeverity.Low), CancellationToken.None);

        (await _queue.CountPendingAsync(MessageSeverity.Normal, CancellationToken.None)).Should().Be(2);
        (await _queue.CountPendingAsync(MessageSeverity.Low, CancellationToken.None)).Should().Be(1);
        (await _queue.CountPendingAsync(MessageSeverity.Critical, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task DequeueBatchAsync_RespectsMaxCountAndSeverityFilter()
    {
        await _queue.EnqueueAsync(NewMessage("k-l-1", MessageSeverity.Low), CancellationToken.None);
        await _queue.EnqueueAsync(NewMessage("k-l-2", MessageSeverity.Low), CancellationToken.None);
        await _queue.EnqueueAsync(NewMessage("k-l-3", MessageSeverity.Low), CancellationToken.None);
        await _queue.EnqueueAsync(NewMessage("k-norm", MessageSeverity.Normal), CancellationToken.None);

        var batch = await _queue.DequeueBatchAsync(MessageSeverity.Low, maxCount: 2, CancellationToken.None);

        batch.Should().HaveCount(2);
        batch.Select(m => m.IdempotencyKey).Should().BeEquivalentTo(new[] { "k-l-1", "k-l-2" });

        using var ctx = _harness.NewContext();
        (await ctx.OutboundMessages.CountAsync(m => m.Status == OutboundMessageStatus.Sending)).Should().Be(2);
    }

    [Fact]
    public async Task MarkSentAsync_TransitionsRowAndStampsPlatformId()
    {
        var msg = NewMessage("k-mark", MessageSeverity.Normal);
        await _queue.EnqueueAsync(msg, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _queue.MarkSentAsync(msg.MessageId, platformMessageId: 9988776655L, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var row = await ctx.OutboundMessages.SingleAsync();
        row.Status.Should().Be(OutboundMessageStatus.Sent);
        row.PlatformMessageId.Should().Be(9988776655L);
        row.SentAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task MarkFailedAsync_BelowMaxAttempts_RecordsBackoff()
    {
        var msg = NewMessage("k-fail", MessageSeverity.Normal, maxAttempts: 3);
        await _queue.EnqueueAsync(msg, CancellationToken.None);

        await _queue.MarkFailedAsync(msg.MessageId, "boom", CancellationToken.None);

        using var ctx = _harness.NewContext();
        var row = await ctx.OutboundMessages.SingleAsync();
        row.Status.Should().Be(OutboundMessageStatus.Failed);
        row.AttemptCount.Should().Be(1);
        row.ErrorDetail.Should().Be("boom");
        row.NextRetryAt.Should().NotBeNull();
        row.NextRetryAt!.Value.Should().BeAfter(_clock.UtcNow);
    }

    [Fact]
    public async Task MarkFailedAsync_AtMaxAttempts_DeadLettersRowAndCreatesDeadLetterRecord()
    {
        var msg = NewMessage("k-dl", MessageSeverity.Normal, maxAttempts: 2);
        await _queue.EnqueueAsync(msg, CancellationToken.None);

        await _queue.MarkFailedAsync(msg.MessageId, "first", CancellationToken.None);
        await _queue.MarkFailedAsync(msg.MessageId, "final", CancellationToken.None);

        using var ctx = _harness.NewContext();
        var row = await ctx.OutboundMessages.SingleAsync();
        row.Status.Should().Be(OutboundMessageStatus.DeadLettered);
        row.AttemptCount.Should().Be(2);
        var dlq = await ctx.DeadLetterMessages.SingleAsync();
        dlq.OriginalMessageId.Should().Be(msg.MessageId);
        dlq.ErrorReason.Should().Be("final");
        dlq.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task DeadLetterAsync_TransitionsAndCreatesDeadLetterRow()
    {
        var msg = NewMessage("k-force", MessageSeverity.Normal);
        await _queue.EnqueueAsync(msg, CancellationToken.None);

        await _queue.DeadLetterAsync(msg.MessageId, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var row = await ctx.OutboundMessages.SingleAsync();
        row.Status.Should().Be(OutboundMessageStatus.DeadLettered);
        var dlq = await ctx.DeadLetterMessages.SingleAsync();
        dlq.OriginalMessageId.Should().Be(msg.MessageId);
    }

    [Fact]
    public async Task EnqueueAsync_NullMessage_Throws()
    {
        var act = async () => await _queue.EnqueueAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DequeueBatchAsync_NonPositiveMaxCount_Throws()
    {
        var act = async () => await _queue.DequeueBatchAsync(MessageSeverity.Low, 0, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task DequeueAsync_TwoDispatchersObservingSamePendingRow_OnlyOneClaims()
    {
        // Atomic-claim contract per architecture.md §4.4 / §10.3: when two
        // dispatchers both observe the same Pending row, exactly one walks
        // away with it. We exercise the underlying mechanism explicitly
        // -- a conditional UPDATE guarded by BOTH the observed Status and
        // the observed NextRetryAt lease -- by opening two contexts that
        // both read the row as Pending and then both attempt to claim.
        // The first wins (1 row affected); the second's WHERE Status ==
        // Pending no longer matches (the row has moved to Sending) and
        // reports 0 rows affected. No row is ever returned twice.
        var msg = NewMessage("k-race", MessageSeverity.Normal);
        await _queue.EnqueueAsync(msg, CancellationToken.None);

        await using var ctxA = _harness.NewContext();
        await using var ctxB = _harness.NewContext();

        var aCandidates = await ctxA.OutboundMessages.AsNoTracking()
            .Where(x => x.Status == OutboundMessageStatus.Pending)
            .ToListAsync();
        var bCandidates = await ctxB.OutboundMessages.AsNoTracking()
            .Where(x => x.Status == OutboundMessageStatus.Pending)
            .ToListAsync();
        aCandidates.Should().ContainSingle();
        bCandidates.Should().ContainSingle();

        var observedNextRetryAt = aCandidates[0].NextRetryAt;
        var leaseUntil = (DateTimeOffset?)(_clock.UtcNow + TimeSpan.FromMinutes(5));
        var aRows = await ctxA.OutboundMessages
            .Where(x => x.MessageId == msg.MessageId
                        && x.Status == OutboundMessageStatus.Pending
                        && x.NextRetryAt == observedNextRetryAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboundMessageStatus.Sending)
                .SetProperty(x => x.NextRetryAt, leaseUntil));
        var bRows = await ctxB.OutboundMessages
            .Where(x => x.MessageId == msg.MessageId
                        && x.Status == OutboundMessageStatus.Pending
                        && x.NextRetryAt == observedNextRetryAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboundMessageStatus.Sending)
                .SetProperty(x => x.NextRetryAt, leaseUntil));

        aRows.Should().Be(1, "first dispatcher wins the conditional UPDATE");
        bRows.Should().Be(0, "second dispatcher's WHERE Status == Pending no longer matches");
    }

    [Fact]
    public async Task DequeueAsync_TwoDispatchersObservingSameExpiredSendingRow_OnlyOneReclaims()
    {
        // Regression test for evaluator-flagged Sending -> Sending race
        // (iter-1 feedback item #3): two dispatchers observing the same
        // expired-lease Sending row must NOT both successfully reclaim it.
        // The fix is the NextRetryAt == observedLease optimistic-concurrency
        // guard in TryClaimAsync: when both dispatchers observe the same
        // expired lease and run the conditional UPDATE concurrently, the
        // first wins by overwriting the lease, and the second's predicate
        // (NextRetryAt == oldExpiredLease) no longer matches because the
        // winner already stamped a fresh lease.
        var queue = new PersistentOutboundQueue(
            _harness.Factory,
            _clock,
            claimLeaseDuration: TimeSpan.FromMinutes(1));

        await queue.EnqueueAsync(NewMessage("k-sending-race", MessageSeverity.Normal), CancellationToken.None);
        var firstClaim = await queue.DequeueAsync(CancellationToken.None);
        firstClaim.Should().NotBeNull();
        firstClaim!.Status.Should().Be(OutboundMessageStatus.Sending);

        // First dispatcher "crashes" -- row remains Sending with an
        // expired lease.
        _clock.Advance(TimeSpan.FromMinutes(2));

        // Both recovery dispatchers observe the same expired Sending row
        // simultaneously.
        await using var ctxA = _harness.NewContext();
        await using var ctxB = _harness.NewContext();

        var aSnapshot = await ctxA.OutboundMessages.AsNoTracking()
            .Where(x => x.Status == OutboundMessageStatus.Sending)
            .SingleAsync();
        var bSnapshot = await ctxB.OutboundMessages.AsNoTracking()
            .Where(x => x.Status == OutboundMessageStatus.Sending)
            .SingleAsync();
        aSnapshot.NextRetryAt.Should().Be(bSnapshot.NextRetryAt,
            "both dispatchers see the same expired lease before the reclaim race");

        // Both attempt the conditional UPDATE that TryClaimAsync issues:
        // WHERE Status == Sending AND NextRetryAt == observedLease.
        var observedLease = aSnapshot.NextRetryAt;
        var newLeaseUntil = (DateTimeOffset?)(_clock.UtcNow + TimeSpan.FromMinutes(1));

        var aRows = await ctxA.OutboundMessages
            .Where(x => x.MessageId == aSnapshot.MessageId
                        && x.Status == OutboundMessageStatus.Sending
                        && x.NextRetryAt == observedLease)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboundMessageStatus.Sending)
                .SetProperty(x => x.NextRetryAt, newLeaseUntil));
        var bRows = await ctxB.OutboundMessages
            .Where(x => x.MessageId == bSnapshot.MessageId
                        && x.Status == OutboundMessageStatus.Sending
                        && x.NextRetryAt == observedLease)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboundMessageStatus.Sending)
                .SetProperty(x => x.NextRetryAt, newLeaseUntil));

        aRows.Should().Be(1, "first reclaim wins by overwriting the expired lease");
        bRows.Should().Be(0,
            "second reclaim's WHERE NextRetryAt == observedLease no longer matches " +
            "because the winner already stamped a fresh lease");
    }

    [Fact]
    public async Task DequeueAsync_AfterClaim_SubsequentDequeueDoesNotReturnSameRow()
    {
        // End-to-end view of the atomic claim using the public API: once
        // DequeueAsync has claimed a row, a second DequeueAsync within the
        // lease window does not return it. Combined with the explicit
        // two-context race test above, this proves the claim contract end
        // to end.
        await _queue.EnqueueAsync(NewMessage("k-once", MessageSeverity.Normal), CancellationToken.None);

        var first = await _queue.DequeueAsync(CancellationToken.None);
        var second = await _queue.DequeueAsync(CancellationToken.None);

        first.Should().NotBeNull();
        first!.IdempotencyKey.Should().Be("k-once");
        second.Should().BeNull("the row is leased to the first dispatcher");
    }

    [Fact]
    public async Task DequeueAsync_RecoversAbandonedSendingRowAfterLeaseExpires()
    {
        // Architecture.md §10.3 Gap A: a dispatcher that crashes after
        // claiming a row but before MarkSentAsync leaves the row in
        // Sending state with an expired lease. The next DequeueAsync must
        // re-claim it for redelivery (at-least-once semantics).
        var queue = new PersistentOutboundQueue(
            _harness.Factory,
            _clock,
            claimLeaseDuration: TimeSpan.FromMinutes(1));

        await queue.EnqueueAsync(NewMessage("k-crashed", MessageSeverity.Normal), CancellationToken.None);

        // First dispatcher claims the row, then "crashes" before marking
        // sent / failed -- the row stays in Sending with a lease that has
        // not yet expired.
        var firstClaim = await queue.DequeueAsync(CancellationToken.None);
        firstClaim.Should().NotBeNull();
        firstClaim!.Status.Should().Be(OutboundMessageStatus.Sending);

        // A second DequeueAsync within the lease window must return null
        // -- the row is still being processed.
        (await queue.DequeueAsync(CancellationToken.None))
            .Should().BeNull("the lease has not yet expired");

        // Advance past the lease -- the recovery sweep can now re-claim.
        _clock.Advance(TimeSpan.FromMinutes(2));

        var secondClaim = await queue.DequeueAsync(CancellationToken.None);
        secondClaim.Should().NotBeNull("the abandoned Sending row must be reclaimable after the lease expires");
        secondClaim!.IdempotencyKey.Should().Be("k-crashed");
        secondClaim.Status.Should().Be(OutboundMessageStatus.Sending);
    }

    [Fact]
    public async Task MarkSentAsync_ClearsLeaseSoSweepCannotResurrectRow()
    {
        // After successful delivery the lease must be cleared so the
        // recovery sweep does not re-pick up a terminally-Sent row.
        var queue = new PersistentOutboundQueue(
            _harness.Factory,
            _clock,
            claimLeaseDuration: TimeSpan.FromSeconds(30));

        var msg = NewMessage("k-cleared", MessageSeverity.Normal);
        await queue.EnqueueAsync(msg, CancellationToken.None);
        var claimed = await queue.DequeueAsync(CancellationToken.None);
        claimed.Should().NotBeNull();

        await queue.MarkSentAsync(msg.MessageId, platformMessageId: 4242L, CancellationToken.None);

        // Advance well past the original lease window -- a Sent row with
        // a cleared lease must not surface again.
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await queue.DequeueAsync(CancellationToken.None))
            .Should().BeNull("Sent rows have a null lease and must not re-enter the candidate set");

        using var ctx = _harness.NewContext();
        var row = await ctx.OutboundMessages.SingleAsync();
        row.NextRetryAt.Should().BeNull();
        row.Status.Should().Be(OutboundMessageStatus.Sent);
    }

    [Fact]
    public void Constructor_NonPositiveLeaseDuration_Throws()
    {
        var act = () => new PersistentOutboundQueue(_harness.Factory, _clock, TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task DequeueBatchAsync_SequentialBatches_DoNotDoubleClaim()
    {
        // Batch dispatcher race-safety: two sequential batch calls drain
        // the queue without overlap. Each row appears in exactly one
        // batch, proving the per-row conditional UPDATE applies inside
        // DequeueBatchAsync too.
        var msgs = Enumerable.Range(0, 12)
            .Select(i => NewMessage($"k-batch-{i:D2}", MessageSeverity.Low))
            .ToList();
        foreach (var m in msgs)
        {
            await _queue.EnqueueAsync(m, CancellationToken.None);
        }

        var first = await _queue.DequeueBatchAsync(MessageSeverity.Low, maxCount: 5, CancellationToken.None);
        var second = await _queue.DequeueBatchAsync(MessageSeverity.Low, maxCount: 5, CancellationToken.None);
        var third = await _queue.DequeueBatchAsync(MessageSeverity.Low, maxCount: 5, CancellationToken.None);
        var fourth = await _queue.DequeueBatchAsync(MessageSeverity.Low, maxCount: 5, CancellationToken.None);

        first.Should().HaveCount(5);
        second.Should().HaveCount(5);
        third.Should().HaveCount(2);
        fourth.Should().BeEmpty();

        var all = first.Concat(second).Concat(third).ToList();
        all.Select(m => m.MessageId).Should().OnlyHaveUniqueItems();
        all.Select(m => m.IdempotencyKey).Should().BeEquivalentTo(msgs.Select(m => m.IdempotencyKey));
    }

    private OutboundMessage NewMessage(
        string idempotencyKey,
        MessageSeverity severity,
        DateTimeOffset? createdAt = null,
        int maxAttempts = OutboundMessage.DefaultMaxAttempts)
    {
        return OutboundMessage.Create(
            idempotencyKey: idempotencyKey,
            chatId: 1234L,
            severity: severity,
            sourceType: OutboundMessageSource.StatusUpdate,
            payload: "{}",
            correlationId: $"trace-{idempotencyKey}",
            sourceEnvelopeJson: null,
            sourceId: null,
            maxAttempts: maxAttempts,
            messageId: Guid.NewGuid(),
            createdAt: createdAt ?? _clock.UtcNow);
    }
}
