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

        await queue.DeadLetterAsync(dequeued!.MessageId, CancellationToken.None);

        var state = queue.Enqueued.Single(m => m.MessageId == dequeued.MessageId);
        state.Status.Should().Be(OutboundMessageStatus.DeadLettered);
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
