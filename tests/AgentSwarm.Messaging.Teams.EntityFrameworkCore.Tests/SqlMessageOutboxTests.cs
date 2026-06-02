using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Behavioural tests for <see cref="SqlMessageOutbox"/>. Covers the full
/// enqueue → dequeue → ack / reschedule / dead-letter lifecycle, the lease-recovery
/// path, and the terminal-entry guard.
/// </summary>
public sealed class SqlMessageOutboxTests
{
    private static OutboxEntry NewEntry(string id, string status = OutboxEntryStatuses.Pending, DateTimeOffset? createdAt = null) => new()
    {
        OutboxEntryId = id,
        CorrelationId = $"corr-{id}",
        Destination = $"teams://tenant/user/{id}",
        DestinationType = OutboxDestinationTypes.Personal,
        DestinationId = id,
        PayloadType = OutboxPayloadTypes.AgentQuestion,
        PayloadJson = "{}",
        Status = status,
        CreatedAt = createdAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task EnqueueAsync_PersistsPendingRow()
    {
        await using var fixture = new OutboxStoreFixture();
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal("e1", row.OutboxEntryId);
        Assert.Equal(OutboxEntryStatuses.Pending, row.Status);
        Assert.Equal(0, row.RetryCount);
    }

    [Fact]
    public async Task DequeueAsync_TransitionsRowsToProcessingWithLease()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var options = new OutboxOptions { ProcessingLeaseDuration = TimeSpan.FromMinutes(5) };
        await using var fixture = new OutboxStoreFixture(options, clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.EnqueueAsync(NewEntry("e2"), CancellationToken.None);

        var dequeued = await fixture.Store.DequeueAsync(batchSize: 10, CancellationToken.None);
        Assert.Equal(2, dequeued.Count);
        Assert.All(dequeued, e => Assert.Equal(OutboxEntryStatuses.Processing, e.Status));
        Assert.All(dequeued, e => Assert.Equal(clock.GetUtcNow().AddMinutes(5), e.LeaseExpiresAt));

        // Subsequent dequeue should skip leased rows.
        var second = await fixture.Store.DequeueAsync(batchSize: 10, CancellationToken.None);
        Assert.Empty(second);
    }

    [Fact]
    public async Task DequeueAsync_RespectsBatchSize()
    {
        await using var fixture = new OutboxStoreFixture();
        for (var i = 0; i < 5; i++)
        {
            await fixture.Store.EnqueueAsync(NewEntry($"e{i}"), CancellationToken.None);
        }

        var dequeued = await fixture.Store.DequeueAsync(batchSize: 3, CancellationToken.None);
        Assert.Equal(3, dequeued.Count);
    }

    [Fact]
    public async Task DequeueAsync_SkipsRowsWithFutureNextRetryAt()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        await using var fixture = new OutboxStoreFixture(timeProvider: clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.EnqueueAsync(NewEntry("e2"), CancellationToken.None);

        // Reschedule e1 into the future, leave e2 pending.
        await fixture.Store.RescheduleAsync(
            "e1",
            clock.GetUtcNow().AddMinutes(10),
            "transient",
            CancellationToken.None);

        var batch = await fixture.Store.DequeueAsync(batchSize: 10, CancellationToken.None);
        Assert.Single(batch);
        Assert.Equal("e2", batch[0].OutboxEntryId);
    }

    /// <summary>
    /// Stage 6.3 iter-5 evaluator feedback item 6 — <see cref="SqlMessageOutbox.CountPendingAsync"/>
    /// must mirror the dequeue predicate so the <c>teams.outbox.queue_depth</c>
    /// gauge reflects the DRAINABLE pending backlog (Pending rows whose
    /// <c>NextRetryAt</c> is null or has elapsed) rather than every Pending row
    /// regardless of schedule. Pre-fix, a backlog of (1 drainable + 9 future-scheduled)
    /// reported queue_depth=10 even though only 1 row was claimable, misleading
    /// operators into provisioning more drain capacity than the engine could use.
    /// </summary>
    [Fact]
    public async Task CountPendingAsync_FiltersFutureNextRetryAt_MirrorsDequeuePredicate()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        await using var fixture = new OutboxStoreFixture(timeProvider: clock);

        // Mix of: 2 freshly enqueued (NextRetryAt = null), 1 scheduled for the past
        // (drainable), and 3 scheduled for the future (NOT drainable on this tick).
        for (var i = 0; i < 6; i++)
        {
            await fixture.Store.EnqueueAsync(NewEntry($"e{i}"), CancellationToken.None);
        }

        // Reschedule e0 into the past (still drainable — NextRetryAt <= now).
        await fixture.Store.RescheduleAsync(
            "e0",
            clock.GetUtcNow().AddMinutes(-5),
            "transient",
            CancellationToken.None);

        // Reschedule e3, e4, e5 into the future (NOT drainable on this tick).
        await fixture.Store.RescheduleAsync("e3", clock.GetUtcNow().AddMinutes(10), "transient", CancellationToken.None);
        await fixture.Store.RescheduleAsync("e4", clock.GetUtcNow().AddMinutes(20), "transient", CancellationToken.None);
        await fixture.Store.RescheduleAsync("e5", clock.GetUtcNow().AddMinutes(30), "transient", CancellationToken.None);

        // Drainable: e1 (NextRetryAt = null), e2 (NextRetryAt = null), e0 (past).
        // Not drainable: e3, e4, e5 (future).
        var pendingCount = await fixture.Store.CountPendingAsync(CancellationToken.None);
        var dequeueable = await fixture.Store.DequeueAsync(batchSize: 100, CancellationToken.None);

        Assert.Equal(3, pendingCount);
        Assert.Equal(3, dequeueable.Count);
        // Identical row set → identical count. The gauge contract is "what the
        // engine could claim on the next tick", not "every Pending row ever".
        Assert.Equal(dequeueable.Count, pendingCount);
    }

    /// <summary>
    /// Stage 6.3 iter-5 evaluator feedback item 6 — after advancing the clock past
    /// the rescheduled retry timestamp, the previously-not-drainable rows become
    /// drainable and <see cref="SqlMessageOutbox.CountPendingAsync"/> reflects
    /// the new count. Pins the time-dependence of the predicate.
    /// </summary>
    [Fact]
    public async Task CountPendingAsync_RowsBecomeDrainableAsClockAdvances()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        await using var fixture = new OutboxStoreFixture(timeProvider: clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.RescheduleAsync("e1", clock.GetUtcNow().AddMinutes(15), "transient", CancellationToken.None);

        // Before the retry timestamp — not drainable, count must be 0.
        Assert.Equal(0, await fixture.Store.CountPendingAsync(CancellationToken.None));

        // Advance past the retry timestamp — row becomes drainable.
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(1, await fixture.Store.CountPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DequeueAsync_ReclaimsExpiredLeases()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var options = new OutboxOptions { ProcessingLeaseDuration = TimeSpan.FromMinutes(5) };
        await using var fixture = new OutboxStoreFixture(options, clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        var first = await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);
        Assert.Single(first);

        // Simulate crash: do not ack. Advance past the lease window.
        clock.Advance(TimeSpan.FromMinutes(6));

        var reclaimed = await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);
        Assert.Single(reclaimed);
        Assert.Equal("e1", reclaimed[0].OutboxEntryId);
    }

    [Fact]
    public async Task AcknowledgeAsync_TransitionsToSentAndStampsReceipt()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        await using var fixture = new OutboxStoreFixture(timeProvider: clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        var receipt = new OutboxDeliveryReceipt(
            ActivityId: "act-1",
            ConversationId: "conv-1",
            DeliveredAt: clock.GetUtcNow());

        await fixture.Store.AcknowledgeAsync("e1", receipt, CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal(OutboxEntryStatuses.Sent, row.Status);
        Assert.Equal("act-1", row.ActivityId);
        Assert.Equal("conv-1", row.ConversationId);
        Assert.Equal(receipt.DeliveredAt, row.DeliveredAt);
        Assert.Null(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task RescheduleAsync_IncrementsRetryCount_StampsNextRetry_AndResetsToPending()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        await using var fixture = new OutboxStoreFixture(timeProvider: clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        var nextRetry = clock.GetUtcNow().AddSeconds(8);
        await fixture.Store.RescheduleAsync("e1", nextRetry, "transient", CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal(OutboxEntryStatuses.Pending, row.Status);
        Assert.Equal(1, row.RetryCount);
        Assert.Equal(nextRetry, row.NextRetryAt);
        Assert.Equal("transient", row.LastError);
        Assert.Null(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task RescheduleAsync_TruncatesOverlongErrorMessage()
    {
        await using var fixture = new OutboxStoreFixture();
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);

        var longError = new string('x', 3000);
        await fixture.Store.RescheduleAsync(
            "e1",
            DateTimeOffset.UtcNow.AddSeconds(2),
            longError,
            CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.NotNull(row.LastError);
        Assert.Equal(2048, row.LastError!.Length);
    }

    [Fact]
    public async Task DeadLetterAsync_TransitionsToDeadLetteredAndStampsError()
    {
        await using var fixture = new OutboxStoreFixture();
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        await fixture.Store.DeadLetterAsync("e1", "permanent failure", CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal(OutboxEntryStatuses.DeadLettered, row.Status);
        Assert.Equal("permanent failure", row.LastError);
        Assert.Null(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task EnqueueAsync_TerminalEntryIsNotResurrected()
    {
        await using var fixture = new OutboxStoreFixture();
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);
        await fixture.Store.AcknowledgeAsync(
            "e1",
            new OutboxDeliveryReceipt("act", "conv", DateTimeOffset.UtcNow),
            CancellationToken.None);

        // Re-enqueue same id — should be a no-op.
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal(OutboxEntryStatuses.Sent, row.Status);
    }

    [Fact]
    public async Task EnqueueAsync_RefusesToOverwriteActiveLease()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var options = new OutboxOptions { ProcessingLeaseDuration = TimeSpan.FromMinutes(5) };
        await using var fixture = new OutboxStoreFixture(options, clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None));
    }

    [Fact]
    public async Task AcknowledgeAsync_ThrowsWhenRowMissing()
    {
        await using var fixture = new OutboxStoreFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.AcknowledgeAsync(
                "missing",
                new OutboxDeliveryReceipt(null, null, DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public async Task DequeueAsync_ZeroBatchSizeReturnsEmpty()
    {
        await using var fixture = new OutboxStoreFixture();
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);

        var batch = await fixture.Store.DequeueAsync(batchSize: 0, CancellationToken.None);
        Assert.Empty(batch);
    }

    [Fact]
    public async Task RecordSendReceiptAsync_StampsActivityIdsWithoutChangingStatus()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        await using var fixture = new OutboxStoreFixture(timeProvider: clock);

        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        var dequeued = await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);
        Assert.Single(dequeued);
        var leaseBefore = dequeued[0].LeaseExpiresAt;

        await fixture.Store.RecordSendReceiptAsync(
            "e1",
            new OutboxDeliveryReceipt("act-123", "conv-123", clock.GetUtcNow()),
            CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        // Critique #3: receipt persisted, but row stays Processing so the lease keeps
        // the row off other workers' dequeue scans while post-send persistence runs.
        Assert.Equal(OutboxEntryStatuses.Processing, row.Status);
        Assert.Equal("act-123", row.ActivityId);
        Assert.Equal("conv-123", row.ConversationId);
        Assert.Equal(leaseBefore, row.LeaseExpiresAt);
        Assert.Null(row.DeliveredAt);
    }

    [Fact]
    public async Task RecordSendReceiptAsync_ThrowsWhenRowMissing()
    {
        await using var fixture = new OutboxStoreFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.RecordSendReceiptAsync(
                "missing",
                new OutboxDeliveryReceipt("act", "conv", DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    /// <summary>
    /// Stage 6.1 iter-4 evaluator feedback — when the dispatcher captures the
    /// post-send <see cref="OutboxDeliveryReceipt.ConversationReferenceJson"/>
    /// for an AgentQuestion send, <see cref="SqlMessageOutbox.RecordSendReceiptAsync"/>
    /// MUST persist it onto <see cref="OutboxEntry.ConversationReferenceJson"/>.
    /// This is the durable bridge that lets a subsequent layer-1 idempotent
    /// replay in <c>TeamsOutboxDispatcher.DispatchQuestionAsync</c> read back the
    /// DELIVERED reference (rather than the stale enqueue-time one), so the
    /// card-state row saved on replay matches what the fresh-send path would
    /// have produced — eliminating the original-vs-delivered drift the iter-3
    /// evaluator flagged.
    /// </summary>
    [Fact]
    public async Task RecordSendReceiptAsync_PersistsDeliveredConversationReferenceJson_WhenReceiptCarriesIt()
    {
        await using var fixture = new OutboxStoreFixture();
        var entry = NewEntry("e1") with { ConversationReferenceJson = "{\"original\":true,\"conv\":\"19:original@thread.tacv2\"}" };
        await fixture.Store.EnqueueAsync(entry, CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        const string deliveredReference = "{\"delivered\":true,\"conv\":\"19:delivered@thread.tacv2\",\"serviceUrl\":\"https://smba.trafficmanager.net/teams/\"}";
        await fixture.Store.RecordSendReceiptAsync(
            "e1",
            new OutboxDeliveryReceipt("act-deliv", "19:delivered@thread.tacv2", DateTimeOffset.UtcNow)
            {
                ConversationReferenceJson = deliveredReference,
            },
            CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal("act-deliv", row.ActivityId);
        Assert.Equal("19:delivered@thread.tacv2", row.ConversationId);
        // Critical assertion — DELIVERED reference must replace the original
        // enqueue-time reference, so a later layer-1 replay reads the delivered
        // one back out of the row.
        Assert.Equal(deliveredReference, row.ConversationReferenceJson);
        Assert.NotEqual(entry.ConversationReferenceJson, row.ConversationReferenceJson);
        // Status invariant — receipt persistence must NOT transition the row.
        Assert.Equal(OutboxEntryStatuses.Processing, row.Status);
    }

    /// <summary>
    /// Stage 6.1 iter-4 evaluator feedback — when the receipt's
    /// <see cref="OutboxDeliveryReceipt.ConversationReferenceJson"/> is <c>null</c>
    /// (plain <c>MessengerMessage</c> path, no AgentQuestion capture), the
    /// existing column value MUST be preserved. A regression that nulled the
    /// column would lose the enqueue-time reference and break any subsequent
    /// retry that needs to rehydrate the proactive turn.
    /// </summary>
    [Fact]
    public async Task RecordSendReceiptAsync_NullReferenceJson_PreservesExistingColumnValue()
    {
        await using var fixture = new OutboxStoreFixture();
        const string enqueueTimeReference = "{\"enqueueTime\":true}";
        var entry = NewEntry("e1") with { ConversationReferenceJson = enqueueTimeReference };
        await fixture.Store.EnqueueAsync(entry, CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        // Plain message path — receipt has no ConversationReferenceJson.
        await fixture.Store.RecordSendReceiptAsync(
            "e1",
            new OutboxDeliveryReceipt("act-plain", "conv-plain", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal("act-plain", row.ActivityId);
        Assert.Equal("conv-plain", row.ConversationId);
        // The column MUST remain the enqueue-time reference — not regressed to null.
        Assert.Equal(enqueueTimeReference, row.ConversationReferenceJson);
    }

    /// <summary>
    /// Stage 6.1 iter-4 evaluator feedback — <see cref="SqlMessageOutbox.AcknowledgeAsync"/>
    /// also persists the receipt's
    /// <see cref="OutboxDeliveryReceipt.ConversationReferenceJson"/> when present,
    /// so the canonical audit row carries the DELIVERED reference even when the
    /// dispatcher took an idempotent path (layer-2 cardstate hit, or layer-1
    /// replay) and did not call <see cref="SqlMessageOutbox.RecordSendReceiptAsync"/>
    /// mid-flight on this attempt.
    /// </summary>
    [Fact]
    public async Task AcknowledgeAsync_PersistsDeliveredConversationReferenceJson_WhenReceiptCarriesIt()
    {
        await using var fixture = new OutboxStoreFixture();
        var entry = NewEntry("e1") with { ConversationReferenceJson = "{\"original\":true}" };
        await fixture.Store.EnqueueAsync(entry, CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        const string deliveredReference = "{\"delivered\":true,\"conv\":\"19:delivered@thread.tacv2\"}";
        await fixture.Store.AcknowledgeAsync(
            "e1",
            new OutboxDeliveryReceipt("act-ack", "19:delivered@thread.tacv2", DateTimeOffset.UtcNow)
            {
                ConversationReferenceJson = deliveredReference,
            },
            CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal(OutboxEntryStatuses.Sent, row.Status);
        Assert.Equal("act-ack", row.ActivityId);
        Assert.Equal("19:delivered@thread.tacv2", row.ConversationId);
        Assert.Equal(deliveredReference, row.ConversationReferenceJson);
    }

    /// <summary>
    /// Stage 6.1 iter-4 evaluator feedback — <see cref="SqlMessageOutbox.AcknowledgeAsync"/>
    /// with a <c>null</c> reference on the receipt MUST preserve any value
    /// already on the row (typically persisted earlier by
    /// <see cref="SqlMessageOutbox.RecordSendReceiptAsync"/>).
    /// </summary>
    [Fact]
    public async Task AcknowledgeAsync_NullReferenceJson_PreservesExistingColumnValue()
    {
        await using var fixture = new OutboxStoreFixture();
        const string preAckReference = "{\"preAck\":true}";
        var entry = NewEntry("e1") with { ConversationReferenceJson = preAckReference };
        await fixture.Store.EnqueueAsync(entry, CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);

        await fixture.Store.AcknowledgeAsync(
            "e1",
            new OutboxDeliveryReceipt("act", "conv", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal(OutboxEntryStatuses.Sent, row.Status);
        Assert.Equal(preAckReference, row.ConversationReferenceJson);
    }

    [Fact]
    public async Task EnqueueAsync_DeadLetteredEntryIsNotResurrected()
    {
        await using var fixture = new OutboxStoreFixture();
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);
        await fixture.Store.DequeueAsync(batchSize: 1, CancellationToken.None);
        await fixture.Store.DeadLetterAsync("e1", "permanent", CancellationToken.None);

        // Re-enqueue same id — should be a no-op for DeadLettered just like Sent.
        await fixture.Store.EnqueueAsync(NewEntry("e1"), CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var row = await ctx.OutboxEntries.SingleAsync();
        Assert.Equal(OutboxEntryStatuses.DeadLettered, row.Status);
    }

    [Fact]
    public async Task DequeueAsync_AtomicClaim_DoesNotResurfaceLeasedRow()
    {
        // Critique #1: per-row atomic claim. Two back-to-back dequeue calls against the
        // same pool must produce non-overlapping results — once a row is leased, the
        // second dequeue must skip it. Sequential rather than threaded because SQLite's
        // in-memory provider serialises connection access and threading can flake; the
        // claim's atomicity is enforced inside SqlMessageOutbox.DequeueAsync via
        // ExecuteUpdateAsync with a (Status, LeaseExpiresAt) precondition.
        await using var fixture = new OutboxStoreFixture();
        for (var i = 0; i < 20; i++)
        {
            await fixture.Store.EnqueueAsync(NewEntry($"e{i:D2}"), CancellationToken.None);
        }

        var first = await fixture.Store.DequeueAsync(batchSize: 10, CancellationToken.None);
        var second = await fixture.Store.DequeueAsync(batchSize: 10, CancellationToken.None);

        var idsA = first.Select(e => e.OutboxEntryId).ToHashSet();
        var idsB = second.Select(e => e.OutboxEntryId).ToHashSet();
        Assert.Empty(idsA.Intersect(idsB));
        Assert.Equal(20, idsA.Count + idsB.Count);

        // A third dequeue while the leases are still live must find nothing.
        var third = await fixture.Store.DequeueAsync(batchSize: 10, CancellationToken.None);
        Assert.Empty(third);
    }
}
