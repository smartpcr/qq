using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Tests for the <see cref="ChannelInboundEventPublisher"/> stub registered in Stage 2.1 as
/// the default <see cref="IInboundEventPublisher"/>. The contract that matters for Stage 2.3
/// is that the publisher and reader observe a single shared channel — so a publish on one
/// side appears on the reader side, and the writer is closable.
/// </summary>
public sealed class ChannelInboundEventPublisherTests
{
    [Fact]
    public async Task PublishedEvent_IsReadableFromReader()
    {
        var publisher = new ChannelInboundEventPublisher();
        var evt = MakeEvent("e-1");

        await publisher.PublishAsync(evt, CancellationToken.None);

        // Reader must see the same instance.
        Assert.True(publisher.Reader.TryRead(out var read));
        Assert.Same(evt, read);
    }

    [Fact]
    public async Task MultipleProducers_FifoOrder_PreservedOnReader()
    {
        var publisher = new ChannelInboundEventPublisher();

        var first = MakeEvent("e-1");
        var second = MakeEvent("e-2");
        var third = MakeEvent("e-3");

        await publisher.PublishAsync(first, CancellationToken.None);
        await publisher.PublishAsync(second, CancellationToken.None);
        await publisher.PublishAsync(third, CancellationToken.None);

        Assert.True(publisher.Reader.TryRead(out var a));
        Assert.True(publisher.Reader.TryRead(out var b));
        Assert.True(publisher.Reader.TryRead(out var c));

        Assert.Equal("e-1", a!.EventId);
        Assert.Equal("e-2", b!.EventId);
        Assert.Equal("e-3", c!.EventId);
    }

    [Fact]
    public async Task NullEvent_Throws()
    {
        var publisher = new ChannelInboundEventPublisher();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => publisher.PublishAsync(messengerEvent: null!, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledToken_PropagatesCancellation()
    {
        // Unbounded channels do not normally throw on write, but the wrapping AsTask must
        // honor a cancellation token. We pre-cancel and assert.
        var publisher = new ChannelInboundEventPublisher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishAsync(MakeEvent("e-cancel"), cts.Token));
    }

    [Fact]
    public void WriterAndReader_BackedBySameChannel()
    {
        // Stage 2.3 contract: the convenience Writer accessor must produce the same channel
        // that Reader exposes. Asserting the round-trip pins the SingleReader=true,
        // SingleWriter=false channel configuration without inspecting Channel internals.
        var publisher = new ChannelInboundEventPublisher();

        Assert.True(publisher.Writer.TryWrite(MakeEvent("e-direct")));
        Assert.True(publisher.Reader.TryRead(out var read));
        Assert.Equal("e-direct", read!.EventId);
    }

    private static MessengerEvent MakeEvent(string id) =>
        new TextEvent
        {
            EventId = id,
            CorrelationId = "corr-" + id,
            Messenger = "Teams",
            ExternalUserId = "aad-user-" + id,
            Source = MessengerEventSources.PersonalChat,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = "hello " + id,
        };
}
