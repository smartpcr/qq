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
