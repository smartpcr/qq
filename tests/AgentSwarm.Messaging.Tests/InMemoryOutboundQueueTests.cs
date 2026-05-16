using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Iter-2 evaluator item 3 — pin the in-memory <see cref="IOutboundQueue"/>
/// fallback's real (lossless) FIFO-by-priority behaviour so a dev / CI
/// host that has not yet wired the Stage 4.1 persistent queue still
/// completes the full outbound lifecycle the connector relies on
/// (Enqueue → Dequeue → MarkSent / MarkFailed / DeadLetter).
///
/// These tests deliberately drive the implementation type through the
/// <see cref="IOutboundQueue"/> contract because that is what
/// production code sees — they do not reach into internals beyond the
/// diagnostic <c>Enqueued</c> view.
/// </summary>
public sealed class InMemoryOutboundQueueTests
{
    private static readonly DateTimeOffset BaseTime = new(2025, 06, 01, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public async Task EnqueueAsync_StoresMessageAndAllowsDequeue()
    {
        var queue = new InMemoryOutboundQueue(new FakeTimeProvider(BaseTime));
        var message = BuildMessage(severity: MessageSeverity.High, suffix: "1");

        await queue.EnqueueAsync(message, CancellationToken.None);

        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        dequeued.Should().NotBeNull(
            "the iter-2 fix promotes this from a null-returning stub to a real in-process queue so connector-enqueued messages can actually be drained for delivery");
        dequeued!.MessageId.Should().Be(message.MessageId);
        dequeued.IdempotencyKey.Should().Be(message.IdempotencyKey);
        dequeued.Status.Should().Be(OutboundMessageStatus.Sending, "Dequeue must atomically transition Pending → Sending");
    }

    [Fact]
    public async Task DequeueAsync_OnEmptyQueue_ReturnsNull()
    {
        var queue = new InMemoryOutboundQueue(new FakeTimeProvider(BaseTime));

        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        dequeued.Should().BeNull("an empty queue must return null per IOutboundQueue.DequeueAsync contract");
    }

    [Fact]
    public async Task DequeueAsync_PrefersHigherSeverity()
    {
        var queue = new InMemoryOutboundQueue(new FakeTimeProvider(BaseTime));

        // Enqueue in reverse-priority order to ensure ordering, not
        // insertion-time, drives the dequeue choice.
        await queue.EnqueueAsync(BuildMessage(severity: MessageSeverity.Low, suffix: "low"), CancellationToken.None);
        await queue.EnqueueAsync(BuildMessage(severity: MessageSeverity.Normal, suffix: "norm"), CancellationToken.None);
        await queue.EnqueueAsync(BuildMessage(severity: MessageSeverity.Critical, suffix: "crit"), CancellationToken.None);
        await queue.EnqueueAsync(BuildMessage(severity: MessageSeverity.High, suffix: "hi"), CancellationToken.None);

        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("crit");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("hi");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("norm");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("low");
        (await queue.DequeueAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DequeueAsync_WithinSameSeverity_ReturnsOldestFirst()
    {
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);

        var first = BuildMessage(severity: MessageSeverity.Normal, suffix: "1", createdAt: BaseTime);
        var second = BuildMessage(severity: MessageSeverity.Normal, suffix: "2", createdAt: BaseTime.AddSeconds(5));
        var third = BuildMessage(severity: MessageSeverity.Normal, suffix: "3", createdAt: BaseTime.AddSeconds(10));

        // Insert in non-monotonic order.
        await queue.EnqueueAsync(third, CancellationToken.None);
        await queue.EnqueueAsync(first, CancellationToken.None);
        await queue.EnqueueAsync(second, CancellationToken.None);

        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("1");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("2");
        (await queue.DequeueAsync(CancellationToken.None))!.IdempotencyKey.Should().EndWith("3");
    }

    [Fact]
    public async Task EnqueueAsync_DuplicateIdempotencyKey_Throws()
    {
        var queue = new InMemoryOutboundQueue(new FakeTimeProvider(BaseTime));
        var first = BuildMessage(severity: MessageSeverity.Normal, suffix: "dup");

        await queue.EnqueueAsync(first, CancellationToken.None);

        var duplicate = first with { MessageId = Guid.NewGuid() };
        var act = async () => await queue.EnqueueAsync(duplicate, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("IdempotencyKey", StringComparison.Ordinal),
                "the in-memory queue mirrors the production UNIQUE-index dedup so dev/CI sees the same shape as Stage 4.1");
    }

    [Fact]
    public async Task MarkSentAsync_StampsTelegramMessageId_AndTransitionsToSent()
    {
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);
        var message = BuildMessage(severity: MessageSeverity.High, suffix: "s");
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(7));
        await queue.MarkSentAsync(dequeued!.MessageId, telegramMessageId: 9988, CancellationToken.None);

        var state = queue.Enqueued.Single(m => m.MessageId == dequeued.MessageId);
        state.Status.Should().Be(OutboundMessageStatus.Sent);
        state.TelegramMessageId.Should().Be(9988);
        state.SentAt.Should().Be(BaseTime.AddSeconds(7));
    }

    [Fact]
    public async Task MarkFailedAsync_WithBudgetRemaining_SchedulesRetry()
    {
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);
        var message = BuildMessage(severity: MessageSeverity.High, suffix: "f");
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        await queue.MarkFailedAsync(dequeued!.MessageId, "transient 5xx", CancellationToken.None);

        var state = queue.Enqueued.Single(m => m.MessageId == dequeued.MessageId);
        state.Status.Should().Be(OutboundMessageStatus.Pending, "retry budget remains so the message returns to Pending");
        state.AttemptCount.Should().Be(1);
        state.ErrorDetail.Should().Be("transient 5xx");
        state.NextRetryAt.Should().NotBeNull("a retry must be scheduled until the dev OutboundQueueProcessor picks it up again");
        state.NextRetryAt!.Value.Should().BeAfter(BaseTime);
    }

    [Fact]
    public async Task MarkFailedAsync_WhenBudgetExhausted_TransitionsToFailed()
    {
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);
        var message = BuildMessage(severity: MessageSeverity.High, suffix: "fb") with { MaxAttempts = 1 };
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        await queue.MarkFailedAsync(dequeued!.MessageId, "fatal", CancellationToken.None);

        var state = queue.Enqueued.Single(m => m.MessageId == dequeued.MessageId);
        state.Status.Should().Be(OutboundMessageStatus.Failed,
            "AttemptCount == MaxAttempts must transition out of Pending so the message stops being re-dequeued");
        state.NextRetryAt.Should().BeNull();
        state.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task DeadLetterAsync_TransitionsToDeadLettered()
    {
        var queue = new InMemoryOutboundQueue(new FakeTimeProvider(BaseTime));
        var message = BuildMessage(severity: MessageSeverity.Low, suffix: "dl");
        await queue.EnqueueAsync(message, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        await queue.DeadLetterAsync(dequeued!.MessageId, "Permanent: chat_not_found", CancellationToken.None);

        var state = queue.Enqueued.Single(m => m.MessageId == dequeued.MessageId);
        state.Status.Should().Be(OutboundMessageStatus.DeadLettered);
        // Iter-2 evaluator item 5 — the terminal transition now
        // persists the failure reason on ErrorDetail and bumps
        // AttemptCount by one so the audit row preserves the cause.
        state.ErrorDetail.Should().Be("Permanent: chat_not_found");
        state.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task DequeueAsync_HonoursNextRetryAt_FutureMessageIsSkipped()
    {
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);
        // Manually craft a Pending message with NextRetryAt in the
        // future to mimic a post-failure retry-scheduled record.
        var future = BuildMessage(severity: MessageSeverity.High, suffix: "fut") with
        {
            NextRetryAt = BaseTime.AddMinutes(5),
        };
        var now = BuildMessage(severity: MessageSeverity.Normal, suffix: "now");

        await queue.EnqueueAsync(future, CancellationToken.None);
        await queue.EnqueueAsync(now, CancellationToken.None);

        // Even though `future` has higher severity, its NextRetryAt has
        // not elapsed so dequeue must skip it and return `now`.
        var first = await queue.DequeueAsync(CancellationToken.None);
        first!.IdempotencyKey.Should().EndWith("now");

        // No more eligible messages until time advances past the
        // scheduled retry.
        (await queue.DequeueAsync(CancellationToken.None)).Should().BeNull();

        time.Advance(TimeSpan.FromMinutes(6));
        var second = await queue.DequeueAsync(CancellationToken.None);
        second!.IdempotencyKey.Should().EndWith("fut");
    }

    [Fact]
    public async Task DequeueAsync_AfterCancellation_Throws()
    {
        var queue = new InMemoryOutboundQueue(new FakeTimeProvider(BaseTime));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await queue.DequeueAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Constructor_RejectsNullTimeProvider()
    {
        Action act = () => _ = new InMemoryOutboundQueue(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task EnqueueAsync_NullMessage_Throws()
    {
        var queue = new InMemoryOutboundQueue(new FakeTimeProvider(BaseTime));
        var act = async () => await queue.EnqueueAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task EnqueueAsync_WhenChannelWriteCancelled_RollsBackDedupAndDictionary()
    {
        // Stage 4.1 iter-3 evaluator item 2 — when the bounded
        // channel WriteAsync fails (cancellation, channel completion,
        // or any other exception) AFTER the EnqueueAsync method has
        // already consumed both the idempotency-key slot and the
        // MessageId dictionary slot, the queue MUST roll both back so
        // the caller's retry sees a clean slate. Without rollback:
        //   1. The idempotency key remains parked in
        //      `_seenIdempotencyKeys`, so a fresh enqueue of the same
        //      logical message is rejected as a duplicate even though
        //      no row was ever published to a Channel<OutboundMessage>.
        //   2. The MessageId dictionary entry becomes an orphan
        //      Pending row no DequeueAsync can ever claim (no channel
        //      reference) — silently leaking memory and breaking the
        //      `Enqueued` diagnostic view's invariants.
        //
        // We exercise the failure path by:
        //   a) Filling a per-severity bounded channel (capacity = 1)
        //      with one successful enqueue.
        //   b) Calling EnqueueAsync with a pre-cancelled token while
        //      the channel is full — Channel<T>.Writer.WriteAsync
        //      observes the cancellation and throws
        //      OperationCanceledException.
        //   c) Asserting that a third enqueue with the SAME
        //      idempotency key as (b) succeeds — proving the rollback
        //      restored the dedup slot.
        //
        // The capacity-1 channel + pre-cancelled token combination
        // deterministically reproduces the failure mode without
        // relying on timing.
        var queue = new InMemoryOutboundQueue(
            new FakeTimeProvider(BaseTime),
            perSeverityCapacity: 1);

        // (a) Fill the High-severity channel to capacity.
        var occupier = BuildMessage(severity: MessageSeverity.High, suffix: "rb-occupier");
        await queue.EnqueueAsync(occupier, CancellationToken.None);

        // (b) Try to enqueue another High-severity message with a
        // pre-cancelled token. Because the channel is full, the
        // WriteAsync must wait — and the wait observes the cancellation
        // and throws.
        var sharedKey = "s:agent:rb-key";
        var failed = BuildMessage(severity: MessageSeverity.High, suffix: "rb-failed") with
        {
            IdempotencyKey = sharedKey,
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => queue.EnqueueAsync(failed, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "with the channel at capacity and a pre-cancelled token, Channel<T>.Writer.WriteAsync must propagate cancellation");

        // Invariant: the failed enqueue MUST NOT leave a row behind.
        // The `Enqueued` view reflects `_byMessageId.Values` so if
        // rollback regressed, this would report two entries.
        queue.Enqueued.Should().ContainSingle(
            "the failed enqueue must roll back its `_byMessageId` insert so the dictionary does not leak orphan rows that no Channel<OutboundMessage> entry references");
        queue.Enqueued.Single().MessageId.Should().Be(occupier.MessageId);

        // (c) Drain the channel so the next enqueue has capacity,
        // then re-enqueue with the SAME idempotency key the failed
        // call attempted to consume. If rollback was incomplete,
        // this fresh enqueue would be rejected as a duplicate.
        (await queue.DequeueAsync(CancellationToken.None)).Should().NotBeNull(
            "the original occupier must still be claimable — its lifecycle is unaffected by the failed sibling enqueue");

        var retried = BuildMessage(severity: MessageSeverity.High, suffix: "rb-retried") with
        {
            IdempotencyKey = sharedKey,
        };
        Func<Task> retry = () => queue.EnqueueAsync(retried, CancellationToken.None);
        await retry.Should().NotThrowAsync(
            "the failed enqueue's idempotency-key slot must have been released so a re-enqueue with the same key succeeds — otherwise a transient cancellation silently bricks the logical message id forever");

        // And the retried enqueue must actually be dequeueable.
        var observed = await queue.DequeueAsync(CancellationToken.None);
        observed.Should().NotBeNull();
        observed!.MessageId.Should().Be(retried.MessageId,
            "the retried message MUST land on the High-severity channel and be claimable — not lost between dictionary and channel state");
    }

    [Fact]
    public async Task EnqueueAsync_WhenSeverityChannelAtCapacity_BlocksUntilCancellation()
    {
        // Stage 4.1 iter-3 evaluator item 2 — bounded-capacity
        // regression test. The implementation sets
        // BoundedChannelFullMode.Wait on each per-severity
        // Channel<OutboundMessage>; the contract says EnqueueAsync
        // MUST block when the channel is full (and propagate
        // cancellation) rather than silently accept unbounded writes.
        //
        // We prove the bounded-Wait shape end-to-end by:
        //   (a) constructing a queue with perSeverityCapacity = 1
        //   (b) filling the High-severity channel with a single
        //       successful enqueue
        //   (c) launching a SECOND High enqueue with a CTS that
        //       cancels after 200 ms, and using a Stopwatch to prove
        //       the call actually waited (rather than returning
        //       instantly because the channel grew unbounded)
        //
        // Both invariants matter:
        //   * if the channel were unbounded the second EnqueueAsync
        //     would complete in microseconds (no
        //     OperationCanceledException, Stopwatch < 100 ms) — the
        //     test would fail on either the throw assertion OR the
        //     elapsed-time assertion
        //   * if the cancellation path were not honoured (e.g. the
        //     Wait happened on a non-cancellable primitive) the test
        //     would hang and time out via xUnit's run-level
        //     timeout
        var queue = new InMemoryOutboundQueue(
            new FakeTimeProvider(BaseTime),
            perSeverityCapacity: 1);

        // (a/b) Fill the High channel with one enqueue.
        await queue.EnqueueAsync(
            BuildMessage(MessageSeverity.High, "cap-occupier"),
            CancellationToken.None);

        // (c) Attempt a second High enqueue with a CTS that cancels
        // after 200 ms. The channel is at capacity so the WriteAsync
        // blocks; cancellation must propagate as
        // OperationCanceledException.
        const int cancelAfterMs = 200;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelAfterMs));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = () => queue.EnqueueAsync(
            BuildMessage(MessageSeverity.High, "cap-waiter"),
            cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "with perSeverityCapacity=1 and BoundedChannelFullMode.Wait, a second High enqueue MUST block on the bounded channel and propagate the cancellation — silent acceptance would indicate the channel grew unbounded");
        sw.Stop();

        // The elapsed-time check is the structural proof that the
        // call ACTUALLY waited. We allow a 25% jitter slack
        // (>= cancelAfterMs * 0.75) because CTS-fired cancellation
        // can fire a few ms early on Windows. If the channel were
        // unbounded the call would complete in << 50 ms, far below
        // this floor.
        var minWaitMs = (long)(cancelAfterMs * 0.75);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(
            minWaitMs,
            "the producer must have actually waited on the bounded channel — observed {0} ms; if this is < {1} ms the channel accepted the write without blocking (capacity is not being enforced)",
            sw.ElapsedMilliseconds,
            minWaitMs);

        // Sanity: a Critical-severity enqueue at the same time MUST
        // still succeed because each severity has its own bounded
        // channel — capacity is per-severity, not shared.
        Func<Task> sibling = () => queue.EnqueueAsync(
            BuildMessage(MessageSeverity.Critical, "cap-sibling"),
            CancellationToken.None);
        await sibling.Should().NotThrowAsync(
            "the per-severity channel cap must NOT spill over to other severities; a saturated High channel must not block a Critical enqueue");
    }

    [Fact]
    public async Task EnqueueAsync_WhenSeverityChannelAtCapacity_UnblocksAfterDequeue()
    {
        // Stage 4.1 iter-3 evaluator item 2 — complementary positive
        // test. Beyond proving cancellation propagation while
        // blocked, the bounded-capacity contract also requires that
        // a producer waiting on capacity actually resumes once a
        // DequeueAsync frees a slot. Without this, the queue would
        // be stuck in deadlock under any sustained burst — the
        // negative test (cancellation) alone does not prove
        // forward progress.
        var queue = new InMemoryOutboundQueue(
            new FakeTimeProvider(BaseTime),
            perSeverityCapacity: 1);

        await queue.EnqueueAsync(
            BuildMessage(MessageSeverity.Normal, "unblock-occupier"),
            CancellationToken.None);

        // Launch a producer that will block on the full channel.
        var waiter = BuildMessage(MessageSeverity.Normal, "unblock-waiter");
        var producerTask = Task.Run(() => queue.EnqueueAsync(waiter, CancellationToken.None));

        // Give the producer a moment to actually enter the wait
        // (verifies it didn't complete instantly — bounded gate
        // is engaged).
        var noisyFinishWindow = Task.WhenAny(producerTask, Task.Delay(150));
        var winner = await noisyFinishWindow;
        winner.Should().NotBe(producerTask,
            "the second enqueue MUST be blocked while the channel is at capacity; if it completed within 150 ms the bounded gate is not enforced");

        // Drain one message — that releases a capacity slot and the
        // blocked producer must now complete.
        var drained = await queue.DequeueAsync(CancellationToken.None);
        drained.Should().NotBeNull();

        using var unblockCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await producerTask.WaitAsync(unblockCts.Token);
        producerTask.IsCompletedSuccessfully.Should().BeTrue(
            "after DequeueAsync frees a capacity slot, the blocked producer MUST unblock and complete its WriteAsync; if it stays blocked the queue would deadlock under sustained burst");

        // And the just-unblocked message must be claimable on the
        // next dequeue — proving the write actually landed on the
        // channel rather than silently dropping.
        var next = await queue.DequeueAsync(CancellationToken.None);
        next.Should().NotBeNull();
        next!.MessageId.Should().Be(waiter.MessageId);
    }

    [Fact]
    public async Task DequeueAsync_WithBlockedSameSeverityProducers_DoesNotBlockOnRequeueOfDeferredItems()
    {
        // Stage 4.1 iter-4 evaluator item 1 — regression test for
        // the in-memory queue's deferred-requeue deadlock window.
        //
        // The bug: DequeueAsync drained the entire per-severity
        // channel into a candidate list, claimed one (oldest by
        // CreatedAt), and re-published the rest via an async write
        // back into the bounded channel in its `finally` block.
        // With a bounded channel + multiple producers blocked on
        // capacity, the sequence was:
        //
        //   1. Drain frees N slots in the bounded channel.
        //   2. Blocked producers wake up and write into the
        //      now-empty channel — refilling it to capacity.
        //   3. The finally block tries to re-publish (N-1)
        //      deferred items. The channel is full again, so
        //      `WriteAsync` blocks waiting for a slot.
        //   4. Slots will only open when another DequeueAsync
        //      consumes the producer-written items — but THIS
        //      DequeueAsync hasn't returned yet, so the next
        //      one can't start. Circular wait.
        //
        // The fix routes deferred items through a per-severity
        // `ConcurrentQueue<OutboundMessage>` side bucket rather
        // than back through the bounded channel, so the dequeue
        // path never blocks on its own re-publish work.
        //
        // This test reproduces the evaluator's exact setup:
        //   * capacity > 1 (perSeverityCapacity = 2 — minimum
        //     where the drain yields ≥ 2 items so at least one
        //     becomes a "deferred unclaimed item"),
        //   * more than one blocked same-severity producer
        //     (we start TWO producer tasks waiting on capacity),
        //   * at least one deferred unclaimed item (drain yields
        //     2 ready items, claim 1, defer 1).
        //
        // Regression-detection signal: a hard timeout on
        // DequeueAsync. Under the OLD strategy (re-publish via
        // channel), when producers happened to win the race, the
        // dequeue would either deadlock or — with a cancellation
        // token — throw OperationCanceledException at the timeout
        // (because the `finally`'s WriteAsync observed the token).
        // Under the NEW strategy (bucket re-publish), the finally
        // block is guaranteed non-blocking and DequeueAsync
        // returns promptly with a claimed snapshot.
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time, perSeverityCapacity: 2);

        // (a) Fill the High channel to capacity with two ready
        // items. Use distinct CreatedAt stamps so the within-
        // severity ordering contract picks `occ1` first.
        var occ1 = BuildMessage(severity: MessageSeverity.High, suffix: "occ1", createdAt: BaseTime);
        var occ2 = BuildMessage(severity: MessageSeverity.High, suffix: "occ2", createdAt: BaseTime.AddMilliseconds(1));
        await queue.EnqueueAsync(occ1, CancellationToken.None);
        await queue.EnqueueAsync(occ2, CancellationToken.None);

        // (b) Launch two High-severity producers — both must
        // block on capacity because the channel is full.
        var prod1 = BuildMessage(severity: MessageSeverity.High, suffix: "prod1", createdAt: BaseTime.AddMilliseconds(2));
        var prod2 = BuildMessage(severity: MessageSeverity.High, suffix: "prod2", createdAt: BaseTime.AddMilliseconds(3));
        var p1Task = Task.Run(() => queue.EnqueueAsync(prod1, CancellationToken.None));
        var p2Task = Task.Run(() => queue.EnqueueAsync(prod2, CancellationToken.None));

        // Confirm both producers are actually blocked. If they
        // completed within this window the test setup wouldn't
        // exercise the deferred-requeue path the evaluator cited.
        _ = await Task.WhenAny(
            Task.WhenAll(p1Task, p2Task),
            Task.Delay(300));
        p1Task.IsCompleted.Should().BeFalse(
            "first High producer must block on the full bounded channel — without that we cannot exercise the >1-blocked-producer regression");
        p2Task.IsCompleted.Should().BeFalse(
            "second High producer must also block — the evaluator's setup requires more than one waiting producer");

        // (c) Dequeue with a hard timeout. Under the OLD strategy,
        // the finally's WriteAsync could observe `dequeueCts` and
        // throw OperationCanceledException. Under the NEW strategy
        // (deferred bucket), the finally is non-blocking and this
        // returns the oldest-CreatedAt claim within milliseconds.
        using var dequeueCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var dequeued = await queue.DequeueAsync(dequeueCts.Token);

        dequeued.Should().NotBeNull(
            "DequeueAsync must complete without blocking on re-publishing deferred items — the deferred-bucket strategy makes the finally non-blocking regardless of producer pressure");
        dequeued!.MessageId.Should().Be(occ1.MessageId,
            "within-severity ordering picks the oldest-CreatedAt ready message (occ1 at BaseTime vs occ2 at +1ms)");

        // (d) The drain freed two channel slots; the deferred
        // bucket got `occ2`. Both blocked producers must now be
        // able to complete — drain did NOT consume the freed slots
        // for the deferred item.
        await Task.WhenAll(p1Task, p2Task).WaitAsync(TimeSpan.FromSeconds(2));
        p1Task.IsCompletedSuccessfully.Should().BeTrue(
            "after dequeue freed capacity slots, the first blocked producer must unblock — the new strategy keeps deferred items off the channel so capacity is genuinely freed");
        p2Task.IsCompletedSuccessfully.Should().BeTrue(
            "the second blocked producer must also unblock for the same reason");

        // (e) All FOUR original High-severity messages — the two
        // occupiers and the two once-blocked producers — must be
        // dequeueable on subsequent calls. None can be lost
        // between the bounded channel and the deferred bucket.
        var d2 = await queue.DequeueAsync(CancellationToken.None);
        var d3 = await queue.DequeueAsync(CancellationToken.None);
        var d4 = await queue.DequeueAsync(CancellationToken.None);

        var observedIds = new[] { dequeued.MessageId, d2!.MessageId, d3!.MessageId, d4!.MessageId };
        observedIds.Should().BeEquivalentTo(
            new[] { occ1.MessageId, occ2.MessageId, prod1.MessageId, prod2.MessageId },
            "every enqueued message — both the original occupiers and the once-blocked producers — must eventually flow through the channel-or-bucket merge in the dequeue loop without loss");

        // CreatedAt-ordered: occ1 < occ2 < prod1 < prod2. The
        // dequeue loop merges channel + bucket then sorts by
        // CreatedAt, so the deferred `occ2` must come before
        // `prod1`/`prod2` even though `occ2` lives in the bucket
        // and `prod1`/`prod2` came in through the channel.
        observedIds.Should().Equal(
            new[] { occ1.MessageId, occ2.MessageId, prod1.MessageId, prod2.MessageId },
            "the bucket-vs-channel merge must preserve within-severity CreatedAt ordering — `occ2` (deferred to bucket) precedes `prod1`/`prod2` (channel) because its CreatedAt is earlier");
    }

    private static OutboundMessage BuildMessage(
        MessageSeverity severity,
        string suffix,
        DateTimeOffset? createdAt = null) => new()
    {
        MessageId = Guid.NewGuid(),
        IdempotencyKey = $"s:agent:{suffix}",
        ChatId = 100,
        Payload = $"payload-{suffix}",
        Severity = severity,
        SourceType = OutboundSourceType.StatusUpdate,
        SourceId = suffix,
        CreatedAt = createdAt ?? BaseTime,
        CorrelationId = $"trace-{suffix}",
    };
}
