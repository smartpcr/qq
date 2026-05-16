using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Queues;

/// <summary>
/// Stage 1.3 behaviour tests for <see cref="ChannelBasedSlackQueue{T}"/>.
/// Covers the two scenarios spelled out in
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// lines 60--61, plus a small amount of additional coverage that the
/// brief implies (cancellation while waiting; null guards) so future
/// stages can swap the in-process queue for a durable one without
/// silently losing semantics.
/// </summary>
public sealed class ChannelBasedSlackQueueTests
{
    [Fact]
    public async Task Three_enqueued_inbound_envelopes_are_returned_in_FIFO_order()
    {
        // Brief scenario: "Given a `ChannelBasedSlackQueue<SlackInboundEnvelope>`,
        // When 3 envelopes are enqueued and dequeued,
        // Then envelopes are returned in FIFO order".
        ChannelBasedSlackQueue<SlackInboundEnvelope> queue = new();

        SlackInboundEnvelope first = MakeInbound("k1");
        SlackInboundEnvelope second = MakeInbound("k2");
        SlackInboundEnvelope third = MakeInbound("k3");

        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);
        await queue.EnqueueAsync(third);

        SlackInboundEnvelope d1 = await queue.DequeueAsync(CancellationToken.None);
        SlackInboundEnvelope d2 = await queue.DequeueAsync(CancellationToken.None);
        SlackInboundEnvelope d3 = await queue.DequeueAsync(CancellationToken.None);

        d1.Should().BeSameAs(first);
        d2.Should().BeSameAs(second);
        d3.Should().BeSameAs(third);
    }

    [Fact]
    public async Task DequeueAsync_throws_OperationCanceledException_when_token_is_already_cancelled()
    {
        // Brief scenario: "Given a `ChannelBasedSlackQueue` with no items,
        // When `DequeueAsync` is called with a cancelled token,
        // Then an `OperationCanceledException` is thrown".
        ChannelBasedSlackQueue<SlackInboundEnvelope> queue = new();

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await queue.DequeueAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DequeueAsync_throws_OperationCanceledException_when_token_is_cancelled_while_waiting()
    {
        // Complements the brief scenario: a consumer that is already
        // blocked on an empty queue must observe cancellation, not hang
        // forever. The Channel<T> contract guarantees this; the test pins
        // it so a future swap-in implementation does not silently regress.
        ChannelBasedSlackQueue<SlackInboundEnvelope> queue = new();

        using CancellationTokenSource cts = new();
        ValueTask<SlackInboundEnvelope> pending = queue.DequeueAsync(cts.Token);
        pending.IsCompleted.Should().BeFalse("there is nothing in the queue to dequeue");

        cts.Cancel();

        Func<Task> act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Outbound_envelopes_round_trip_through_the_generic_queue()
    {
        // The brief explicitly names both envelope types as targets of the
        // generic queue. Asserting against SlackOutboundEnvelope as well
        // protects against a future change that drops the generic
        // constraint or restricts T to inbound only.
        ChannelBasedSlackQueue<SlackOutboundEnvelope> queue = new();

        SlackOutboundEnvelope envelope = new(
            TaskId: "TASK-1",
            CorrelationId: "corr-1",
            MessageType: SlackOutboundOperationKind.PostMessage,
            BlockKitPayload: "{\"blocks\":[]}",
            ThreadTs: null);

        await queue.EnqueueAsync(envelope);
        SlackOutboundEnvelope received = await queue.DequeueAsync(CancellationToken.None);

        received.Should().BeSameAs(envelope);
    }

    [Fact]
    public async Task EnqueueAsync_rejects_null_items()
    {
        ChannelBasedSlackQueue<SlackInboundEnvelope> queue = new();

        Func<Task> act = async () => await queue.EnqueueAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Bounded_constructor_rejects_non_positive_capacity()
    {
        Action zero = () => _ = new ChannelBasedSlackQueue<SlackInboundEnvelope>(0);
        Action negative = () => _ = new ChannelBasedSlackQueue<SlackInboundEnvelope>(-1);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Complete_then_dequeue_drains_then_throws_ChannelClosedException()
    {
        ChannelBasedSlackQueue<SlackInboundEnvelope> queue = new();
        SlackInboundEnvelope only = MakeInbound("k-only");
        await queue.EnqueueAsync(only);

        queue.Complete();

        SlackInboundEnvelope drained = await queue.DequeueAsync(CancellationToken.None);
        drained.Should().BeSameAs(only);

        Func<Task> act = async () => await queue.DequeueAsync(CancellationToken.None);
        await act.Should().ThrowAsync<System.Threading.Channels.ChannelClosedException>();
    }

    private static SlackInboundEnvelope MakeInbound(string idempotencyKey) => new(
        IdempotencyKey: idempotencyKey,
        SourceType: SlackInboundSourceType.Command,
        TeamId: "T-test",
        ChannelId: "C-test",
        UserId: "U-test",
        RawPayload: "{}",
        TriggerId: null,
        ReceivedAt: DateTimeOffset.UtcNow);
}
