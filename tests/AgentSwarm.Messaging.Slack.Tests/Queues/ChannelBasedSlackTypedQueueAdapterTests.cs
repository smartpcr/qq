using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Queues;

/// <summary>
/// Tests for the typed in-process queue adapters
/// (<see cref="ChannelBasedSlackInboundQueue"/> and
/// <see cref="ChannelBasedSlackOutboundQueue"/>). The brief calls out the
/// in-process queue as the dev / test stand-in for the production durable
/// queue, so these tests pin that:
/// (a) each adapter is assignable to its named brief-mandated interface,
/// (b) FIFO ordering is preserved when used through the interface, and
/// (c) cancellation propagates through the interface.
/// </summary>
public sealed class ChannelBasedSlackTypedQueueAdapterTests
{
    [Fact]
    public void Inbound_adapter_is_assignable_to_ISlackInboundQueue()
    {
        ChannelBasedSlackInboundQueue adapter = new();

        adapter.Should().BeAssignableTo<ISlackInboundQueue>(
            because: "the in-process inbound queue MUST satisfy the brief-mandated contract so DI can register it as ISlackInboundQueue");
    }

    [Fact]
    public void Outbound_adapter_is_assignable_to_ISlackOutboundQueue()
    {
        ChannelBasedSlackOutboundQueue adapter = new();

        adapter.Should().BeAssignableTo<ISlackOutboundQueue>(
            because: "the in-process outbound queue MUST satisfy the brief-mandated contract so DI can register it as ISlackOutboundQueue");
    }

    [Fact]
    public async Task Inbound_adapter_preserves_FIFO_when_used_through_the_interface()
    {
        ISlackInboundQueue queue = new ChannelBasedSlackInboundQueue();

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
    public async Task Outbound_adapter_preserves_FIFO_when_used_through_the_interface()
    {
        ISlackOutboundQueue queue = new ChannelBasedSlackOutboundQueue();

        SlackOutboundEnvelope first = MakeOutbound("TASK-1");
        SlackOutboundEnvelope second = MakeOutbound("TASK-2");
        SlackOutboundEnvelope third = MakeOutbound("TASK-3");

        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);
        await queue.EnqueueAsync(third);

        SlackOutboundEnvelope d1 = await queue.DequeueAsync(CancellationToken.None);
        SlackOutboundEnvelope d2 = await queue.DequeueAsync(CancellationToken.None);
        SlackOutboundEnvelope d3 = await queue.DequeueAsync(CancellationToken.None);

        d1.Should().BeSameAs(first);
        d2.Should().BeSameAs(second);
        d3.Should().BeSameAs(third);
    }

    [Fact]
    public async Task Inbound_adapter_propagates_cancellation_through_the_interface()
    {
        ISlackInboundQueue queue = new ChannelBasedSlackInboundQueue();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await queue.DequeueAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Outbound_adapter_propagates_cancellation_through_the_interface()
    {
        ISlackOutboundQueue queue = new ChannelBasedSlackOutboundQueue();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await queue.DequeueAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Inbound_adapter_rejects_null_envelopes()
    {
        ISlackInboundQueue queue = new ChannelBasedSlackInboundQueue();

        Func<Task> act = async () => await queue.EnqueueAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Outbound_adapter_rejects_null_envelopes()
    {
        ISlackOutboundQueue queue = new ChannelBasedSlackOutboundQueue();

        Func<Task> act = async () => await queue.EnqueueAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Adapter_constructors_reject_null_backing_queue()
    {
        Action inbound = () => _ = new ChannelBasedSlackInboundQueue(
            (ChannelBasedSlackQueue<SlackInboundEnvelope>)null!);
        Action outbound = () => _ = new ChannelBasedSlackOutboundQueue(
            (ChannelBasedSlackQueue<SlackOutboundEnvelope>)null!);

        inbound.Should().Throw<ArgumentNullException>();
        outbound.Should().Throw<ArgumentNullException>();
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

    private static SlackOutboundEnvelope MakeOutbound(string taskId) => new(
        TaskId: taskId,
        CorrelationId: $"corr-{taskId}",
        MessageType: SlackOutboundOperationKind.PostMessage,
        BlockKitPayload: "{\"blocks\":[]}",
        ThreadTs: null);
}
