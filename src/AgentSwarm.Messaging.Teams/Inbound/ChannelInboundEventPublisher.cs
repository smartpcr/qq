using System.Threading.Channels;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Inbound;

/// <summary>
/// <see cref="IInboundEventPublisher"/> backed by a <see cref="Channel{T}"/> writer/reader
/// pair. Producers (the Teams activity handler, command dispatcher, and card action handler)
/// call <see cref="PublishAsync"/>; <c>TeamsMessengerConnector.ReceiveAsync</c> (Stage 2.3)
/// reads from the same channel.
/// </summary>
/// <remarks>
/// Stage 2.1 registers this as a singleton so the writer and reader live in the same process
/// and share the same in-memory channel.
/// </remarks>
public sealed class ChannelInboundEventPublisher : IInboundEventPublisher
{
    private readonly Channel<MessengerEvent> _channel
        = Channel.CreateUnbounded<MessengerEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>Reader end consumed by <c>TeamsMessengerConnector.ReceiveAsync</c>.</summary>
    public ChannelReader<MessengerEvent> Reader => _channel.Reader;

    /// <inheritdoc />
    public async Task PublishAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        if (messengerEvent is null) throw new ArgumentNullException(nameof(messengerEvent));
        await _channel.Writer.WriteAsync(messengerEvent, ct).ConfigureAwait(false);
    }
}
