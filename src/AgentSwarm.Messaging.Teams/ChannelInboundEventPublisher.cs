using System.Threading.Channels;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// In-process <see cref="IInboundEventPublisher"/> + <see cref="IInboundEventReader"/>
/// implementation backed by a <see cref="Channel{T}"/> of <see cref="MessengerEvent"/>.
/// Bridges inbound-event producers (<c>TeamsSwarmActivityHandler</c>,
/// <c>CommandDispatcher</c>, <c>CardActionHandler</c>) and the
/// <c>TeamsMessengerConnector.ReceiveAsync</c> consumer.
/// </summary>
/// <remarks>
/// <para>
/// The default constructor uses an unbounded channel — sufficient for the single-process
/// Stage 2.x deployment. Multi-process deployments swap in a Redis or Service Bus backed
/// implementation. The explicit-channel constructor is used by tests to inject a bounded
/// channel and assert backpressure semantics.
/// </para>
/// <para>
/// One singleton instance is registered under both <see cref="IInboundEventPublisher"/> and
/// <see cref="IInboundEventReader"/> (see
/// <c>TeamsServiceCollectionExtensions.AddTeamsMessengerConnector</c>) so that the writer
/// and reader operate on the same backing channel.
/// </para>
/// </remarks>
public sealed class ChannelInboundEventPublisher : IInboundEventPublisher, IInboundEventReader
{
    private readonly Channel<MessengerEvent> _channel;

    /// <summary>Construct the publisher with an unbounded in-memory channel.</summary>
    public ChannelInboundEventPublisher()
        : this(Channel.CreateUnbounded<MessengerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        }))
    {
    }

    /// <summary>
    /// Construct the publisher with an explicit channel. Used by tests to control backing
    /// capacity and reader/writer affinity.
    /// </summary>
    /// <param name="channel">The channel to use for publishing and receiving events.</param>
    public ChannelInboundEventPublisher(Channel<MessengerEvent> channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    /// <inheritdoc />
    public Task PublishAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        if (messengerEvent is null)
        {
            throw new ArgumentNullException(nameof(messengerEvent));
        }

        return _channel.Writer.WriteAsync(messengerEvent, ct).AsTask();
    }

    /// <inheritdoc />
    public async Task<MessengerEvent> ReceiveAsync(CancellationToken ct)
    {
        // Channel<T>.Reader.ReadAsync<T>() returns ValueTask<T>; await it directly to honour
        // cancellation and surface ChannelClosedException when the channel completes.
        return await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
    }
}
