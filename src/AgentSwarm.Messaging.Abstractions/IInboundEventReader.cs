namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Counterpart to <see cref="IInboundEventPublisher"/> exposing the read end of the shared
/// in-process inbound event channel. <see cref="IMessengerConnector.ReceiveAsync"/> consumes
/// events through this interface so that the connector depends on a small abstraction
/// rather than a concrete <c>ChannelInboundEventPublisher</c> implementation.
/// </summary>
/// <remarks>
/// <para>
/// In production the same singleton (the <c>ChannelInboundEventPublisher</c> registered in
/// Stage 2.1 DI) implements both <see cref="IInboundEventPublisher"/> and
/// <see cref="IInboundEventReader"/>. Producers (<c>TeamsSwarmActivityHandler</c>,
/// <c>CommandDispatcher</c>, <c>CardActionHandler</c>) write through the publisher;
/// <see cref="IMessengerConnector.ReceiveAsync"/> reads through this reader.
/// </para>
/// <para>
/// The split mirrors <see cref="System.Threading.Channels.ChannelReader{T}"/> /
/// <see cref="System.Threading.Channels.ChannelWriter{T}"/>: it allows tests to substitute
/// either side independently and prevents accidental coupling of the connector to the
/// publishing surface (the connector should not be able to publish events).
/// </para>
/// </remarks>
public interface IInboundEventReader
{
    /// <summary>
    /// Read the next inbound <see cref="MessengerEvent"/> from the channel. Completes when an
    /// event becomes available, when the channel is completed, or when the supplied
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="ct">Cancellation token used to stop waiting for events.</param>
    /// <returns>The next <see cref="MessengerEvent"/> in arrival order.</returns>
    Task<MessengerEvent> ReceiveAsync(CancellationToken ct);
}
