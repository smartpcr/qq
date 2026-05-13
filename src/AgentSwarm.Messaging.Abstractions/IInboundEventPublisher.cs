namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Decouples inbound event producers (the messenger-specific activity handler, command
/// dispatcher, and card action handler) from the event consumer
/// (<see cref="IMessengerConnector.ReceiveAsync"/>). Implemented in Stage 2.1 by
/// <c>ChannelInboundEventPublisher</c>, which is backed by a
/// <c>System.Threading.Channels.Channel&lt;MessengerEvent&gt;</c>: producers call
/// <see cref="PublishAsync"/> to write to the channel writer, and
/// <see cref="IMessengerConnector.ReceiveAsync"/> reads from the channel reader.
/// </summary>
public interface IInboundEventPublisher
{
    /// <summary>
    /// Publish an inbound <see cref="MessengerEvent"/> to the shared in-process event
    /// channel.
    /// </summary>
    /// <param name="messengerEvent">The event to publish (one of <see cref="CommandEvent"/>, <see cref="DecisionEvent"/>, or <see cref="TextEvent"/>).</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>A task that completes when the event has been written to the channel.</returns>
    Task PublishAsync(MessengerEvent messengerEvent, CancellationToken ct);
}
