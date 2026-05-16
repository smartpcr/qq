using AgentSwarm.Messaging.Slack.Transport;

namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// In-process implementation of <see cref="ISlackOutboundQueue"/> backed by
/// a <see cref="ChannelBasedSlackQueue{T}"/>. Intended for development and
/// unit / integration tests; production deployments register the durable
/// implementation supplied by the upstream <c>AgentSwarm.Messaging.Core</c>
/// project against the same <see cref="ISlackOutboundQueue"/> contract.
/// </summary>
/// <remarks>
/// Composition (not inheritance) is used so the adapter exposes only the
/// brief-mandated <see cref="ISlackOutboundQueue"/> surface and the inner
/// generic queue's richer methods (bounded constructor, channel injection,
/// <see cref="ChannelBasedSlackQueue{T}.Complete"/>) remain available for
/// tests that opt in to them explicitly. Constructed instances can be
/// resolved through DI as <see cref="ISlackOutboundQueue"/> via
/// <c>services.AddSingleton&lt;ISlackOutboundQueue, ChannelBasedSlackOutboundQueue&gt;()</c>.
/// </remarks>
internal sealed class ChannelBasedSlackOutboundQueue : ISlackOutboundQueue
{
    private readonly ChannelBasedSlackQueue<SlackOutboundEnvelope> backing;

    /// <summary>
    /// Initializes a new unbounded in-process outbound queue.
    /// </summary>
    public ChannelBasedSlackOutboundQueue()
        : this(new ChannelBasedSlackQueue<SlackOutboundEnvelope>())
    {
    }

    /// <summary>
    /// Initializes a new bounded in-process outbound queue with the supplied
    /// <paramref name="capacity"/>. Exposed for stress / back-pressure
    /// tests.
    /// </summary>
    public ChannelBasedSlackOutboundQueue(int capacity)
        : this(new ChannelBasedSlackQueue<SlackOutboundEnvelope>(capacity))
    {
    }

    /// <summary>
    /// Initializes a new in-process outbound queue backed by the supplied
    /// generic queue. Exposed so tests can inject a custom channel.
    /// </summary>
    public ChannelBasedSlackOutboundQueue(ChannelBasedSlackQueue<SlackOutboundEnvelope> backing)
    {
        ArgumentNullException.ThrowIfNull(backing);
        this.backing = backing;
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(SlackOutboundEnvelope envelope)
    {
        return this.backing.EnqueueAsync(envelope);
    }

    /// <inheritdoc />
    public ValueTask<SlackOutboundEnvelope> DequeueAsync(CancellationToken ct)
    {
        return this.backing.DequeueAsync(ct);
    }
}
