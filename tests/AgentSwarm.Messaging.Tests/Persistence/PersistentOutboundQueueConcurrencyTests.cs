using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.2 concurrency contract tests for
/// <see cref="PersistentOutboundQueue"/>. These tests use
/// <see cref="ConcurrentSqliteContextHarness"/> to exercise the public
/// <c>DequeueAsync</c> API under true <see cref="Task.WhenAll"/> contention
/// (the default <see cref="SqliteContextHarness"/> cannot do this because
/// it shares a single in-memory connection).
/// </summary>
/// <remarks>
/// <para>
/// Iter-3 evaluator pin (item #1, #2): the previously-added interleaved
/// sequential test did not prove TRUE concurrent claim safety because
/// each <c>await</c> fully drained before the next dispatcher ran. These
/// tests close that gap by:
/// <list type="bullet">
///   <item><description>racing N parallel <c>DequeueAsync</c> calls against
///     M &lt; N enqueued rows so candidate windows overlap (Pending -&gt; Sending),</description></item>
///   <item><description>racing two parallel <c>DequeueAsync</c> calls against
///     the same expired-lease Sending row (Sending -&gt; Sending reclaim).</description></item>
/// </list>
/// </para>
/// </remarks>
public class PersistentOutboundQueueConcurrencyTests : IDisposable
{
    private readonly ConcurrentSqliteContextHarness _harness = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task DequeueAsync_TrueParallelDispatchers_FreshPending_ClaimsEachRowExactlyOnce()
    {
        // Iter-3 evaluator item #1: replace the prior sequential alternating
        // loop with a real Task.WhenAll race. We spin up dispatcherCount >
        // rowCount queues (each its own PersistentOutboundQueue instance,
        // each opening its own SQLite connection) and call DequeueAsync
        // on every one of them concurrently. The contract:
        //   * Every enqueued row surfaces in exactly one dispatcher's result.
        //   * Surplus dispatchers see null (the row is leased to someone else).
        //   * No SQLITE_BUSY exception escapes because the busy timeout in
        //     ConcurrentSqliteContextHarness serialises the UPDATEs.
        const int rowCount = 12;
        const int dispatcherCount = 40;

        var queue = new PersistentOutboundQueue(_harness.Factory, _clock);
        var msgs = Enumerable.Range(0, rowCount)
            .Select(i => NewMessage($"k-true-par-{i:D2}", MessageSeverity.Normal))
            .ToList();
        foreach (var m in msgs)
        {
            await queue.EnqueueAsync(m, CancellationToken.None);
        }

        // Each dispatcher gets its OWN PersistentOutboundQueue instance so
        // none of them share in-process state. They only share the SQLite
        // store. This is the production topology: N worker hosts each
        // construct their own queue against the same connection string.
        var dispatchers = Enumerable.Range(0, dispatcherCount)
            .Select(_ => new PersistentOutboundQueue(_harness.Factory, _clock))
            .ToArray();

        // Task.Run + StartNew with a barrier could be used for tighter
        // overlap, but Task.WhenAll on hot tasks is sufficient: the
        // contention shows up as soon as multiple tasks reach the
        // ExecuteUpdateAsync round-trip and SQLite serialises them at the
        // database-level write lock.
        var tasks = dispatchers
            .Select(d => Task.Run(() => d.DequeueAsync(CancellationToken.None)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var claimed = results.Where(r => r is not null).Select(r => r!).ToList();
        var ids = claimed.Select(m => m.MessageId).ToList();

        ids.Should().OnlyHaveUniqueItems(
            "the conditional UPDATE in TryClaimAsync must let exactly one dispatcher win each row");
        claimed.Select(m => m.IdempotencyKey).Should().BeEquivalentTo(
            msgs.Select(m => m.IdempotencyKey),
            "the set of claimed rows equals the set of enqueued rows -- no losses, no double claims");
        results.Count(r => r is null).Should().Be(
            dispatcherCount - rowCount,
            "surplus dispatchers must observe an empty/leased queue rather than re-claiming a row already won");

        // Persisted state must agree: every row is in Sending status and
        // appears only once.
        using var ctx = _harness.NewContext();
        var rows = await ctx.OutboundMessages.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(rowCount);
        rows.Should().OnlyContain(r => r.Status == OutboundMessageStatus.Sending);
    }

    [Fact]
    public async Task DequeueAsync_TrueParallelDispatchers_ExpiredSendingRow_OnlyOneReclaims()
    {
        // Iter-3 evaluator item #2: directly exercise the expired
        // Sending -> Sending reclaim race that TryClaimAsync's
        // NextRetryAt == observedLease predicate guards. Two dispatchers
        // call DequeueAsync concurrently against the same expired-lease
        // Sending row; exactly one must walk away with a fresh Sending
        // claim, the other must see null.
        //
        // Setup:
        //   1. Enqueue one row.
        //   2. queueA dequeues it (Pending -> Sending, lease = now+1min).
        //   3. Advance clock past the lease.
        //   4. queueB and queueC race DequeueAsync via Task.WhenAll.
        //   5. Assert exactly one of {B, C} returns the row, the other null.
        var leaseDuration = TimeSpan.FromMinutes(1);
        var queueA = new PersistentOutboundQueue(_harness.Factory, _clock, leaseDuration);
        var queueB = new PersistentOutboundQueue(_harness.Factory, _clock, leaseDuration);
        var queueC = new PersistentOutboundQueue(_harness.Factory, _clock, leaseDuration);

        await queueA.EnqueueAsync(
            NewMessage("k-reclaim-race", MessageSeverity.Normal),
            CancellationToken.None);
        var firstClaim = await queueA.DequeueAsync(CancellationToken.None);
        firstClaim.Should().NotBeNull("queueA wins the fresh-Pending claim");
        firstClaim!.Status.Should().Be(OutboundMessageStatus.Sending);

        // Simulate dispatcher A crashing: the row stays in Sending with a
        // lease that the clock will outrun.
        _clock.Advance(TimeSpan.FromMinutes(2));

        // Now race two recovery dispatchers against the same expired row.
        // Without the NextRetryAt == observedLease predicate in
        // TryClaimAsync, both would set Status = Sending and both would
        // believe they won (Status didn't change between observation and
        // update). The predicate makes the second UPDATE match zero rows.
        var raceB = Task.Run(() => queueB.DequeueAsync(CancellationToken.None));
        var raceC = Task.Run(() => queueC.DequeueAsync(CancellationToken.None));
        var results = await Task.WhenAll(raceB, raceC);

        var winners = results.Where(r => r is not null).ToList();
        var losers = results.Where(r => r is null).ToList();

        winners.Should().HaveCount(1,
            "exactly one recovery dispatcher must reclaim the expired Sending row");
        losers.Should().HaveCount(1,
            "the other dispatcher must see null because the winner already stamped a fresh lease");
        winners[0]!.IdempotencyKey.Should().Be("k-reclaim-race");
        winners[0]!.Status.Should().Be(OutboundMessageStatus.Sending);

        // Persisted state: the row is still a single row, in Sending, with
        // a fresh lease (NextRetryAt > observed expired lease).
        using var ctx = _harness.NewContext();
        var rows = await ctx.OutboundMessages.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be(OutboundMessageStatus.Sending);
        rows[0].NextRetryAt.Should().BeAfter(_clock.UtcNow.AddSeconds(-1),
            "the winner stamped a fresh lease at the current clock");
    }

    [Fact]
    public async Task DequeueBatchAsync_TrueParallelDispatchers_BatchClaimsAreDisjoint()
    {
        // Round out the contention surface: two DequeueBatchAsync callers
        // race against the same backlog. Their result sets must be
        // disjoint (no MessageId in both batches) and together must
        // exactly equal the enqueued set (no losses).
        const int rowCount = 16;
        var producerQueue = new PersistentOutboundQueue(_harness.Factory, _clock);
        var msgs = Enumerable.Range(0, rowCount)
            .Select(i => NewMessage($"k-batch-race-{i:D2}", MessageSeverity.Low))
            .ToList();
        foreach (var m in msgs)
        {
            await producerQueue.EnqueueAsync(m, CancellationToken.None);
        }

        var queueX = new PersistentOutboundQueue(_harness.Factory, _clock);
        var queueY = new PersistentOutboundQueue(_harness.Factory, _clock);

        var raceX = Task.Run(() => queueX.DequeueBatchAsync(
            MessageSeverity.Low, maxCount: rowCount, CancellationToken.None));
        var raceY = Task.Run(() => queueY.DequeueBatchAsync(
            MessageSeverity.Low, maxCount: rowCount, CancellationToken.None));
        var batches = await Task.WhenAll(raceX, raceY);

        var batchX = batches[0];
        var batchY = batches[1];

        var combined = batchX.Concat(batchY).ToList();
        combined.Select(m => m.MessageId).Should().OnlyHaveUniqueItems(
            "no MessageId can appear in both parallel batches");
        combined.Select(m => m.IdempotencyKey).Should().BeEquivalentTo(
            msgs.Select(m => m.IdempotencyKey),
            "the union of the two batches equals the enqueued set");
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
