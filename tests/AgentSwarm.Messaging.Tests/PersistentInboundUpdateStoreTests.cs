using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — pins <see cref="PersistentInboundUpdateStore"/> behavior
/// against the acceptance criteria in implementation-plan.md §195-201.
/// Uses a per-test SQLite in-memory connection so the schema, UNIQUE
/// constraint, and EF Core change-tracker behavior match production.
/// </summary>
public sealed class PersistentInboundUpdateStoreTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<MessagingDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        // SQLite in-memory with a shared connection keeps the schema and
        // data alive across DbContext instances within the test, matching
        // a real database session.
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var ctx = new MessagingDbContext(_options);
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private PersistentInboundUpdateStore NewStore(out MessagingDbContext ctx)
    {
        ctx = new MessagingDbContext(_options);
        return new PersistentInboundUpdateStore(
            ctx, NullLogger<PersistentInboundUpdateStore>.Instance);
    }

    private static InboundUpdate NewRow(long updateId, IdempotencyStatus status = IdempotencyStatus.Received) =>
        new()
        {
            UpdateId = updateId,
            RawPayload = "{\"update_id\":" + updateId + "}",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = status,
        };

    [Fact]
    public async Task PersistAsync_NewRow_ReturnsTrue_AndStoresEverything()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        var row = NewRow(101);

        var inserted = await store.PersistAsync(row, CancellationToken.None);

        inserted.Should().BeTrue();
        var roundTrip = await store.GetByUpdateIdAsync(101, CancellationToken.None);
        roundTrip.Should().NotBeNull();
        roundTrip!.UpdateId.Should().Be(101);
        roundTrip.RawPayload.Should().Be(row.RawPayload);
        roundTrip.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
        roundTrip.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task PersistAsync_DuplicateUpdateId_ReturnsFalse_AndDoesNotOverwrite()
    {
        var first = NewStore(out var firstCtx);
        await using var _ = firstCtx;

        var original = NewRow(202) with { RawPayload = "{\"update_id\":202,\"version\":\"first\"}" };
        (await first.PersistAsync(original, CancellationToken.None)).Should().BeTrue();

        // Use a fresh DbContext for the duplicate insert so the change
        // tracker does not pollute the assertion.
        var second = NewStore(out var secondCtx);
        await using var __ = secondCtx;

        var duplicate = NewRow(202) with { RawPayload = "{\"update_id\":202,\"version\":\"second\"}" };
        var result = await second.PersistAsync(duplicate, CancellationToken.None);

        result.Should().BeFalse(
            "the second delivery is a duplicate webhook callback and must NOT execute the command twice");
        var roundTrip = await second.GetByUpdateIdAsync(202, CancellationToken.None);
        roundTrip!.RawPayload.Should().Contain("\"version\":\"first\"",
            "the first persisted payload is the source of truth; duplicates are dropped, not overwritten");
    }

    [Fact]
    public async Task TryMarkProcessingAsync_TransitionsReceivedToProcessing_ReturnsTrueOnce()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(303), CancellationToken.None);

        // First caller wins.
        var firstClaim = await store.TryMarkProcessingAsync(303, CancellationToken.None);
        firstClaim.Should().BeTrue();

        // Second caller observes Processing and loses.
        // Use a fresh store/context so we are not reading from a stale
        // tracked entity (mirrors the dispatcher-vs-sweep races where
        // each runs in its own DI scope).
        var secondStore = NewStore(out var secondCtx);
        await using var __ = secondCtx;

        var secondClaim = await secondStore.TryMarkProcessingAsync(303, CancellationToken.None);
        secondClaim.Should().BeFalse(
            "TryMarkProcessingAsync must be a CAS — only one concurrent caller can claim a Received row");

        var roundTrip = await secondStore.GetByUpdateIdAsync(303, CancellationToken.None);
        roundTrip!.IdempotencyStatus.Should().Be(IdempotencyStatus.Processing);
    }

    [Fact]
    public async Task TryMarkProcessingAsync_TransitionsFailedToProcessing_AllowsRetry()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(404), CancellationToken.None);
        await store.MarkFailedAsync(404, "boom", CancellationToken.None);

        var claim = await store.TryMarkProcessingAsync(404, CancellationToken.None);

        claim.Should().BeTrue(
            "rows in Failed state are eligible for replay; sweep claims them via TryMarkProcessingAsync");
    }

    [Fact]
    public async Task TryMarkProcessingAsync_OnCompletedRow_ReturnsFalse()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(505), CancellationToken.None);
        await store.TryMarkProcessingAsync(505, CancellationToken.None);
        await store.MarkCompletedAsync(505, handlerErrorDetail: null, CancellationToken.None);

        var claim = await store.TryMarkProcessingAsync(505, CancellationToken.None);

        claim.Should().BeFalse(
            "Completed rows are terminal; the sweep must not reclaim them");
    }

    [Fact]
    public async Task TryMarkProcessingAsync_OnMissingRow_ReturnsFalse()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        var claim = await store.TryMarkProcessingAsync(606, CancellationToken.None);

        claim.Should().BeFalse();
    }

    [Fact]
    public async Task ResetInterruptedAsync_FlipsAllProcessingRowsToReceived()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(701), CancellationToken.None);
        await store.PersistAsync(NewRow(702), CancellationToken.None);
        await store.PersistAsync(NewRow(703), CancellationToken.None);

        // Two rows stuck Processing (simulated crash); one stays Received.
        await store.TryMarkProcessingAsync(701, CancellationToken.None);
        await store.TryMarkProcessingAsync(702, CancellationToken.None);

        var resetCount = await store.ResetInterruptedAsync(CancellationToken.None);

        resetCount.Should().Be(2,
            "exactly the two stuck rows transition; the live Received row is unaffected");

        var r701 = await store.GetByUpdateIdAsync(701, CancellationToken.None);
        var r702 = await store.GetByUpdateIdAsync(702, CancellationToken.None);
        var r703 = await store.GetByUpdateIdAsync(703, CancellationToken.None);

        r701!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
        r702!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
        r703!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
    }

    [Fact]
    public async Task ResetInterruptedAsync_DoesNotTouchCompletedOrFailed()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        // Two rows: one Completed, one Failed (with HandlerErrorDetail).
        await store.PersistAsync(NewRow(801), CancellationToken.None);
        await store.TryMarkProcessingAsync(801, CancellationToken.None);
        await store.MarkCompletedAsync(801, "ok", CancellationToken.None);

        await store.PersistAsync(NewRow(802), CancellationToken.None);
        await store.MarkFailedAsync(802, "boom", CancellationToken.None);

        var resetCount = await store.ResetInterruptedAsync(CancellationToken.None);

        resetCount.Should().Be(0);

        var r801 = await store.GetByUpdateIdAsync(801, CancellationToken.None);
        var r802 = await store.GetByUpdateIdAsync(802, CancellationToken.None);

        r801!.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
        r802!.IdempotencyStatus.Should().Be(IdempotencyStatus.Failed);
    }

    [Fact]
    public async Task GetRecoverableAsync_IncludesReceivedProcessingAndFailed_ExcludesCompleted()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(901), CancellationToken.None);                                   // Received
        await store.PersistAsync(NewRow(902), CancellationToken.None);                                   // → Processing
        await store.TryMarkProcessingAsync(902, CancellationToken.None);
        await store.PersistAsync(NewRow(903), CancellationToken.None);                                   // → Completed
        await store.TryMarkProcessingAsync(903, CancellationToken.None);
        await store.MarkCompletedAsync(903, null, CancellationToken.None);
        await store.PersistAsync(NewRow(904), CancellationToken.None);                                   // → Failed
        await store.MarkFailedAsync(904, "transient", CancellationToken.None);

        var recoverable = await store.GetRecoverableAsync(maxRetries: 3, CancellationToken.None);

        recoverable.Select(r => r.UpdateId).Should().BeEquivalentTo(
            new long[] { 901, 902, 904 },
            "architecture.md §4.8 says recoverable includes Received, Processing, AND Failed; Completed rows are terminal and excluded. Processing rows surfacing here are no-ops at the dispatcher's TryMarkProcessing CAS.");
    }

    [Fact]
    public async Task GetRecoverableAsync_ExcludesRowsAtMaxRetries()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(1001), CancellationToken.None);
        // Fail 3 times to hit MaxRetries=3.
        for (var i = 0; i < 3; i++)
        {
            await store.MarkFailedAsync(1001, "attempt " + i, CancellationToken.None);
        }

        var recoverable = await store.GetRecoverableAsync(maxRetries: 3, CancellationToken.None);

        recoverable.Should().BeEmpty(
            "AttemptCount >= MaxRetries means the row is permanently failing and must NOT be replayed");

        var exhaustedCount = await store.GetExhaustedRetryCountAsync(3, CancellationToken.None);
        exhaustedCount.Should().Be(1,
            "the row surfaces via GetExhaustedRetryCountAsync for alerting instead");
    }

    [Fact]
    public async Task MarkCompletedAsync_WithHandlerErrorDetail_RecordsDetail_ButRowStaysCompleted()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(1101), CancellationToken.None);
        await store.TryMarkProcessingAsync(1101, CancellationToken.None);
        await store.MarkCompletedAsync(
            1101,
            handlerErrorDetail: "ErrorCode=ROUTING_DENIED ResponseText=Unauthorized",
            CancellationToken.None);

        var row = await store.GetByUpdateIdAsync(1101, CancellationToken.None);

        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
        row.HandlerErrorDetail.Should().Be("ErrorCode=ROUTING_DENIED ResponseText=Unauthorized");
        row.ErrorDetail.Should().BeNull(
            "ErrorDetail is reserved for UNCAUGHT pipeline exceptions");
        row.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_IncrementsAttemptCount_AndRecordsErrorDetail()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(1201), CancellationToken.None);
        await store.MarkFailedAsync(1201, "boom-1", CancellationToken.None);
        await store.MarkFailedAsync(1201, "boom-2", CancellationToken.None);

        var row = await store.GetByUpdateIdAsync(1201, CancellationToken.None);

        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Failed);
        row.AttemptCount.Should().Be(2);
        row.ErrorDetail.Should().Be("boom-2");
        row.HandlerErrorDetail.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_WithBlankErrorDetail_Throws()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        var act = () => store.MarkFailedAsync(1, "  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ============================================================
    // iter-4 evaluator item 5 — ReleaseProcessingAsync transitions
    // Processing → Received without bumping AttemptCount, supporting
    // the cancel-mid-flight path in InboundUpdateProcessor.
    // ============================================================

    [Fact]
    public async Task ReleaseProcessingAsync_TransitionsProcessingToReceived_LeavingAttemptCountUnchanged()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(2001), CancellationToken.None);
        await store.TryMarkProcessingAsync(2001, CancellationToken.None);

        var released = await store.ReleaseProcessingAsync(2001, CancellationToken.None);

        released.Should().BeTrue("the row was Processing — release must succeed");
        var row = await store.GetByUpdateIdAsync(2001, CancellationToken.None);
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received,
            "release returns the row to the queue without burning a retry attempt");
        row.AttemptCount.Should().Be(0,
            "cancellation is not a pipeline failure; AttemptCount must be unchanged");
        row.ProcessedAt.Should().BeNull(
            "the row hasn't been processed — ProcessedAt must remain unset");
    }

    [Fact]
    public async Task ReleaseProcessingAsync_RowAlreadyCompleted_ReturnsFalse_NoStateChange()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(2002), CancellationToken.None);
        await store.TryMarkProcessingAsync(2002, CancellationToken.None);
        await store.MarkCompletedAsync(2002, null, CancellationToken.None);

        var released = await store.ReleaseProcessingAsync(2002, CancellationToken.None);

        released.Should().BeFalse(
            "the row is no longer in Processing — the CAS guard must reject a stale release");
        var row = await store.GetByUpdateIdAsync(2002, CancellationToken.None);
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed,
            "the late release must NOT downgrade a Completed row back to Received");
    }

    [Fact]
    public async Task ReleaseProcessingAsync_RowMissing_ReturnsFalse()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        var released = await store.ReleaseProcessingAsync(updateId: 9999, CancellationToken.None);

        released.Should().BeFalse();
    }

    // ============================================================
    // iter-4 evaluator item 6 — GetExhaustedAsync returns the actual
    // rows (not just a count) so the recovery sweep can log per-row
    // UpdateId / ErrorDetail at Error level for triage.
    // ============================================================

    [Fact]
    public async Task GetExhaustedAsync_ReturnsRowsWithExhaustedAttempts_OrderedByUpdateId()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(3003), CancellationToken.None);
        await store.PersistAsync(NewRow(3001), CancellationToken.None);
        await store.PersistAsync(NewRow(3002), CancellationToken.None);
        // 3001 + 3002 hit MaxRetries=2; 3003 fails once and is still
        // recoverable.
        await store.MarkFailedAsync(3001, "transient-a", CancellationToken.None);
        await store.MarkFailedAsync(3001, "transient-b", CancellationToken.None);
        await store.MarkFailedAsync(3002, "transient-c", CancellationToken.None);
        await store.MarkFailedAsync(3002, "transient-d", CancellationToken.None);
        await store.MarkFailedAsync(3003, "transient-e", CancellationToken.None);

        var exhausted = await store.GetExhaustedAsync(maxRetries: 2, limit: 50, CancellationToken.None);

        exhausted.Select(r => r.UpdateId).Should().Equal(
            new long[] { 3001, 3002 },
            "rows at or above MaxRetries are exhausted; ordering by UpdateId gives a deterministic per-tick log sequence");
        exhausted[0].ErrorDetail.Should().Be("transient-b",
            "ErrorDetail holds the most recent failure message");
        exhausted[1].ErrorDetail.Should().Be("transient-d");
    }

    [Fact]
    public async Task GetExhaustedAsync_RespectsLimit()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        for (long id = 4001; id <= 4010; id++)
        {
            await store.PersistAsync(NewRow(id), CancellationToken.None);
            await store.MarkFailedAsync(id, "boom", CancellationToken.None);
            await store.MarkFailedAsync(id, "boom", CancellationToken.None);
        }

        var exhausted = await store.GetExhaustedAsync(maxRetries: 2, limit: 3, CancellationToken.None);

        exhausted.Should().HaveCount(3,
            "the limit caps per-tick log-line bursts; triage past the cap belongs to direct DB queries");
        exhausted.Select(r => r.UpdateId).Should().Equal(new long[] { 4001, 4002, 4003 });
    }

    [Fact]
    public async Task GetExhaustedAsync_NoExhaustedRows_ReturnsEmpty()
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        await store.PersistAsync(NewRow(5001), CancellationToken.None);

        var exhausted = await store.GetExhaustedAsync(maxRetries: 3, limit: 10, CancellationToken.None);

        exhausted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetExhaustedAsync_InvalidLimit_Throws(int limit)
    {
        var store = NewStore(out var ctx);
        await using var _ = ctx;

        var act = () => store.GetExhaustedAsync(maxRetries: 3, limit: limit, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
