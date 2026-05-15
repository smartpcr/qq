using System.Threading.Channels;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies the in-process inbound event channel honours the Publish → Receive contract
/// expected by <see cref="TeamsMessengerConnector.ReceiveAsync"/> per
/// <c>implementation-plan.md</c> §2.3 step 4.
/// </summary>
public sealed class ChannelInboundEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_ThenReceiveAsync_RoundTripsTheSameInstance()
    {
        var publisher = new ChannelInboundEventPublisher();
        var ev = NewCommandEvent("status");

        await publisher.PublishAsync(ev, CancellationToken.None);
        var received = await publisher.ReceiveAsync(CancellationToken.None);

        Assert.Same(ev, received);
    }

    [Fact]
    public async Task ReceiveAsync_BeforePublish_AwaitsThePublish()
    {
        var publisher = new ChannelInboundEventPublisher();
        var receive = publisher.ReceiveAsync(CancellationToken.None);
        Assert.False(receive.IsCompleted, "Receive must block until an event is published.");

        var ev = NewCommandEvent("ask");
        await publisher.PublishAsync(ev, CancellationToken.None);

        var received = await receive.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(ev, received);
    }

    [Fact]
    public async Task ReceiveAsync_RespectsCancellation()
    {
        var publisher = new ChannelInboundEventPublisher();
        using var cts = new CancellationTokenSource();

        var receive = publisher.ReceiveAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
    }

    [Fact]
    public async Task PublishAsync_NullEvent_Throws()
    {
        var publisher = new ChannelInboundEventPublisher();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => publisher.PublishAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullChannel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ChannelInboundEventPublisher(null!));
    }

    [Fact]
    public async Task ConcurrentPublishers_AllEventsAreDelivered_InSomeOrder()
    {
        var publisher = new ChannelInboundEventPublisher();
        var producers = new List<Task>();
        for (var i = 0; i < 25; i++)
        {
            var verb = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            producers.Add(Task.Run(() => publisher.PublishAsync(NewCommandEvent("ask", verb), CancellationToken.None)));
        }

        await Task.WhenAll(producers);

        var received = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 25; i++)
        {
            var ev = await publisher.ReceiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            received.Add(ev.EventId);
        }

        Assert.Equal(25, received.Count);
    }

    [Fact]
    public async Task ExplicitChannel_BackpressureDelaysWriter()
    {
        var channel = Channel.CreateBounded<MessengerEvent>(new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var publisher = new ChannelInboundEventPublisher(channel);

        // First publish fits in capacity.
        await publisher.PublishAsync(NewCommandEvent("ask", "1"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        // Second publish must wait until ReceiveAsync drains the first event.
        var blocked = publisher.PublishAsync(NewCommandEvent("ask", "2"), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(blocked.IsCompleted, "Writer must block when channel is full.");

        await publisher.ReceiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await blocked.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static CommandEvent NewCommandEvent(string verb, string? eventId = null)
    {
        return new CommandEvent(MessengerEventTypes.Command)
        {
            EventId = eventId ?? Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString(),
            Messenger = "Teams",
            ExternalUserId = "aad-test-user",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new ParsedCommand(verb, string.Empty, Guid.NewGuid().ToString()),
        };
    }
}
