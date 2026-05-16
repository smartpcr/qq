using AgentSwarm.Messaging.Slack.Transport;

namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// In-process implementation of <see cref="ISlackInboundQueue"/> backed by
/// a <see cref="ChannelBasedSlackQueue{T}"/>. Intended for development and
/// unit / integration tests; production deployments register the durable
/// implementation supplied by the upstream <c>AgentSwarm.Messaging.Core</c>
/// project against the same <see cref="ISlackInboundQueue"/> contract.
/// </summary>
/// <remarks>
/// Composition (not inheritance) is used so the adapter exposes only the
/// brief-mandated <see cref="ISlackInboundQueue"/> surface and the inner
/// generic queue's richer methods (bounded constructor, channel injection,
/// <see cref="ChannelBasedSlackQueue{T}.Complete"/>) remain available for
/// tests that opt in to them explicitly. Constructed instances can be
/// resolved through DI as <see cref="ISlackInboundQueue"/> via
/// <c>services.AddSingleton&lt;ISlackInboundQueue, ChannelBasedSlackInboundQueue&gt;()</c>.
/// </remarks>
internal sealed class ChannelBasedSlackInboundQueue : ISlackInboundQueue
{
    private readonly ChannelBasedSlackQueue<SlackInboundEnvelope> backing;

    /// <summary>
    /// Initializes a new unbounded in-process inbound queue.
    /// </summary>
    public ChannelBasedSlackInboundQueue()
        : this(new ChannelBasedSlackQueue<SlackInboundEnvelope>())
    {
    }

    /// <summary>
    /// Initializes a new bounded in-process inbound queue with the supplied
    /// <paramref name="capacity"/>. Exposed for stress / back-pressure
    /// tests.
    /// </summary>
    public ChannelBasedSlackInboundQueue(int capacity)
        : this(new ChannelBasedSlackQueue<SlackInboundEnvelope>(capacity))
    {
    }

    /// <summary>
    /// Initializes a new in-process inbound queue backed by the supplied
    /// generic queue. Exposed so tests can inject a custom channel.
    /// </summary>
    public ChannelBasedSlackInboundQueue(ChannelBasedSlackQueue<SlackInboundEnvelope> backing)
    {
        ArgumentNullException.ThrowIfNull(backing);
        this.backing = backing;
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(SlackInboundEnvelope envelope)
    {
        return this.backing.EnqueueAsync(envelope);
    }

    /// <inheritdoc />
    public ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct)
    {
        return this.backing.DequeueAsync(ct);
    }
}
