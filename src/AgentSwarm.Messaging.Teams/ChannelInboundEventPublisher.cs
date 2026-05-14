using System.Threading.Channels;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// <see cref="IInboundEventPublisher"/> implementation backed by an unbounded
/// <see cref="Channel{T}"/>. Producers (the Teams activity handler, command dispatcher, and
/// card-action handler) call <see cref="PublishAsync"/> to write into the channel; the
/// <c>TeamsMessengerConnector.ReceiveAsync</c> consumer (Stage 2.3) reads via
/// <see cref="Reader"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton in Stage 2.1 so producers and the consumer share a single
/// channel for the lifetime of the host. Single-process only — multi-instance deployments
/// require a durable bus (e.g., Service Bus, Kafka).
/// </para>
/// <para>
/// The channel is configured with <c>SingleReader = true</c> because
/// <see cref="IMessengerConnector.ReceiveAsync"/> models a single consumer; producers may be
/// multiple, so <c>SingleWriter</c> is left at the default <c>false</c>.
/// </para>
/// </remarks>
public sealed class ChannelInboundEventPublisher : IInboundEventPublisher
{
    private readonly Channel<MessengerEvent> _channel;

    /// <summary>
    /// Initialize a new <see cref="ChannelInboundEventPublisher"/> with an unbounded
    /// single-reader/multi-writer channel.
    /// </summary>
    public ChannelInboundEventPublisher()
    {
        _channel = Channel.CreateUnbounded<MessengerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// The underlying <see cref="ChannelReader{T}"/>. Stage 2.3's
    /// <c>TeamsMessengerConnector.ReceiveAsync</c> reads events from this reader.
    /// </summary>
    public ChannelReader<MessengerEvent> Reader => _channel.Reader;

    /// <summary>
    /// Convenience accessor for the underlying <see cref="ChannelWriter{T}"/>. Tests that
    /// need to inspect the writer directly use this property; production code calls
    /// <see cref="PublishAsync"/>.
    /// </summary>
    public ChannelWriter<MessengerEvent> Writer => _channel.Writer;

    /// <inheritdoc />
    public Task PublishAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        if (messengerEvent is null) throw new ArgumentNullException(nameof(messengerEvent));
        return _channel.Writer.WriteAsync(messengerEvent, ct).AsTask();
    }
}
