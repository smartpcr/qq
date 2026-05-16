using AgentSwarm.Messaging.Discord;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.2 acceptance tests for <see cref="PersistentDiscordInteractionStore"/>.
/// Covers the dedup-on-duplicate scenario plus the
/// Mark*/GetRecoverable lifecycle transitions.
/// </summary>
public class PersistentDiscordInteractionStoreTests : IDisposable
{
    private static readonly TimeSpan TestRecoveryStaleAfter = TimeSpan.FromMinutes(1);

    private readonly SqliteContextHarness _harness = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly PersistentDiscordInteractionStore _store;

    public PersistentDiscordInteractionStoreTests()
    {
        _store = new PersistentDiscordInteractionStore(_harness.Factory, _clock, TestRecoveryStaleAfter);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task PersistAsync_FirstInsert_ReturnsTrueAndPersistsRow()
    {
        var record = NewRecord(interactionId: 1001UL);

        var inserted = await _store.PersistAsync(record, CancellationToken.None);

        inserted.Should().BeTrue();

        using var ctx = _harness.NewContext();
        var stored = await ctx.DiscordInteractions.SingleAsync();
        stored.InteractionId.Should().Be(1001UL);
        stored.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
        stored.ReceivedAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task PersistAsync_DuplicateInteractionId_ReturnsFalseAndKeepsSingleRow()
    {
        // Stage 2.2 Test Scenario: "PersistAsync returns false for duplicate".
        var first = NewRecord(interactionId: 2002UL);
        var second = NewRecord(interactionId: 2002UL);

        (await _store.PersistAsync(first, CancellationToken.None)).Should().BeTrue();
        (await _store.PersistAsync(second, CancellationToken.None)).Should().BeFalse();

        using var ctx = _harness.NewContext();
        (await ctx.DiscordInteractions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task MarkProcessingAsync_IncrementsAttemptAndClearsErrorDetail()
    {
        var record = NewRecord(interactionId: 3003UL);
        record.IdempotencyStatus = IdempotencyStatus.Failed;
        record.ErrorDetail = "previous attempt failed";
        await _store.PersistAsync(record, CancellationToken.None);

        await _store.MarkProcessingAsync(3003UL, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var loaded = await ctx.DiscordInteractions.SingleAsync();
        loaded.IdempotencyStatus.Should().Be(IdempotencyStatus.Processing);
        loaded.AttemptCount.Should().Be(1);
        loaded.ErrorDetail.Should().BeNull();
    }

    [Fact]
    public async Task MarkProcessingAsync_OnCompletedRow_IsNoOp()
    {
        var record = NewRecord(interactionId: 4004UL);
        await _store.PersistAsync(record, CancellationToken.None);
        await _store.MarkProcessingAsync(4004UL, CancellationToken.None);
        await _store.MarkCompletedAsync(4004UL, CancellationToken.None);

        await _store.MarkProcessingAsync(4004UL, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var loaded = await ctx.DiscordInteractions.SingleAsync();
        loaded.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
        loaded.AttemptCount.Should().Be(1); // not re-incremented
    }

    [Fact]
    public async Task MarkCompletedAsync_SetsProcessedAtAndClearsError()
    {
        var record = NewRecord(interactionId: 5005UL);
        record.ErrorDetail = "stale error";
        await _store.PersistAsync(record, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(7));
        await _store.MarkCompletedAsync(5005UL, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var loaded = await ctx.DiscordInteractions.SingleAsync();
        loaded.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
        loaded.ProcessedAt.Should().Be(_clock.UtcNow);
        loaded.ErrorDetail.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_PopulatesErrorDetailAndProcessedAt()
    {
        var record = NewRecord(interactionId: 6006UL);
        await _store.PersistAsync(record, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _store.MarkFailedAsync(6006UL, "rate limit hit", CancellationToken.None);

        using var ctx = _harness.NewContext();
        var loaded = await ctx.DiscordInteractions.SingleAsync();
        loaded.IdempotencyStatus.Should().Be(IdempotencyStatus.Failed);
        loaded.ErrorDetail.Should().Be("rate limit hit");
        loaded.ProcessedAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task GetRecoverableAsync_ExcludesCompletedAndExhausted()
    {
        // Three records: one Completed (excluded), one with too many retries
        // (excluded), one fresh Failed (included). The fresh row's
        // AttemptCount is 1, well below the maxRetries=3 cap.
        var done = NewRecord(interactionId: 7001UL);
        await _store.PersistAsync(done, CancellationToken.None);
        await _store.MarkCompletedAsync(7001UL, CancellationToken.None);

        var exhausted = NewRecord(interactionId: 7002UL);
        exhausted.AttemptCount = 3;
        exhausted.IdempotencyStatus = IdempotencyStatus.Failed;
        await _store.PersistAsync(exhausted, CancellationToken.None);

        var recoverable = NewRecord(interactionId: 7003UL);
        await _store.PersistAsync(recoverable, CancellationToken.None);
        await _store.MarkFailedAsync(7003UL, "transient", CancellationToken.None);

        // Advance the clock past the staleness window so the freshly-Failed
        // row is treated as abandoned (the sweep would otherwise skip it as
        // potentially still being processed by a sibling dispatcher).
        _clock.Advance(TimeSpan.FromMinutes(5));
        var rows = await _store.GetRecoverableAsync(
            maxRetries: 3,
            CancellationToken.None);

        rows.Should().ContainSingle();
        rows[0].InteractionId.Should().Be(7003UL);
    }

    [Fact]
    public async Task GetRecoverableAsync_OrdersByReceivedAtAscending()
    {
        // Persist in reverse temporal order to prove the store re-orders.
        var future = NewRecord(interactionId: 8001UL);
        future.ReceivedAt = _clock.UtcNow - TimeSpan.FromMinutes(5);
        var past = NewRecord(interactionId: 8002UL);
        past.ReceivedAt = _clock.UtcNow - TimeSpan.FromMinutes(15);

        await _store.PersistAsync(future, CancellationToken.None);
        await _store.PersistAsync(past, CancellationToken.None);

        var rows = await _store.GetRecoverableAsync(
            maxRetries: 3,
            CancellationToken.None);

        rows.Select(r => r.InteractionId).Should().ContainInOrder(8002UL, 8001UL);
    }

    [Fact]
    public async Task GetRecoverableAsync_ExcludesRowsInsideStaleWindow()
    {
        // A row that was just touched (within the staleness window) must
        // not be picked up by the recovery sweep -- a sibling dispatcher
        // may still be actively processing it. The staleness threshold
        // is ctor-injected on the store; the public contract no longer
        // surfaces it (architecture.md §4.8 / impl-plan §2.2 line 105).
        // We rebuild the store with a longer staleness window so the
        // just-Failed row falls inside it.
        var longWindowStore = new PersistentDiscordInteractionStore(
            _harness.Factory,
            _clock,
            TimeSpan.FromMinutes(5));
        var freshlyTouched = NewRecord(interactionId: 9001UL);
        await longWindowStore.PersistAsync(freshlyTouched, CancellationToken.None);
        await longWindowStore.MarkFailedAsync(9001UL, "transient", CancellationToken.None);

        // Clock hasn't advanced past 5 minutes; the row's ProcessedAt is
        // still "now", so it falls inside the staleness window.
        var rows = await longWindowStore.GetRecoverableAsync(
            maxRetries: 3,
            CancellationToken.None);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PersistAsync_NullRecord_Throws()
    {
        var act = async () => await _store.PersistAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetRecoverableAsync_NonPositiveMaxRetries_Throws()
    {
        var act = async () => await _store.GetRecoverableAsync(
            maxRetries: 0,
            CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NonPositiveRecoveryStaleAfter_Throws()
    {
        var act = () => new PersistentDiscordInteractionStore(
            _harness.Factory,
            _clock,
            TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private DiscordInteractionRecord NewRecord(ulong interactionId) => new()
    {
        InteractionId = interactionId,
        InteractionType = DiscordInteractionType.SlashCommand,
        GuildId = 999UL,
        ChannelId = 888UL,
        UserId = 777UL,
        RawPayload = "{}",
        ReceivedAt = _clock.UtcNow,
        IdempotencyStatus = IdempotencyStatus.Received,
        AttemptCount = 0,
        ErrorDetail = null,
    };
}
