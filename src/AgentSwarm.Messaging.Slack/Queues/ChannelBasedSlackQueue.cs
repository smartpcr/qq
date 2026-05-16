using System.Threading.Channels;

namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// In-process FIFO queue backed by <see cref="System.Threading.Channels.Channel{T}"/>.
/// Intended for development and unit / integration tests; production
/// deployments swap in a durable queue implementation supplied by the
/// upstream <c>AgentSwarm.Messaging.Core</c> project (see Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Generic over <typeparamref name="T"/> so the same FIFO implementation
/// powers both the inbound and outbound in-process queues. The named
/// brief-mandated interfaces are NOT implemented on this generic type --
/// they are implemented by the thin composition adapters
/// <see cref="ChannelBasedSlackInboundQueue"/> (satisfies
/// <see cref="ISlackInboundQueue"/>, <c>T = SlackInboundEnvelope</c>) and
/// <see cref="ChannelBasedSlackOutboundQueue"/> (satisfies
/// <see cref="ISlackOutboundQueue"/>, <c>T = SlackOutboundEnvelope</c>),
/// which keeps the public adapter surface aligned with the literal Stage
/// 1.3 contract while retaining a richer impl-only surface here for tests
/// (bounded constructor, channel injection,
/// <see cref="ChannelBasedSlackQueue{T}.Complete"/>).
/// </para>
/// <para>
/// Default construction uses an unbounded channel; pass a custom
/// <see cref="System.Threading.Channels.BoundedChannelOptions"/> or
/// <see cref="System.Threading.Channels.UnboundedChannelOptions"/> via the
/// alternative constructor to enable back-pressure for stress tests.
/// </para>
/// </remarks>
/// <typeparam name="T">Element type buffered by the queue.</typeparam>
internal sealed class ChannelBasedSlackQueue<T>
    where T : class
{
    private readonly Channel<T> channel;

    /// <summary>
    /// Initializes a new unbounded multi-producer / multi-consumer queue.
    /// </summary>
    public ChannelBasedSlackQueue()
        : this(Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        }))
    {
    }

    /// <summary>
    /// Initializes a new bounded queue with the supplied capacity. The
    /// queue waits on <see cref="EnqueueAsync"/> when full (full
    /// back-pressure mode) -- callers can override with a different
    /// <see cref="BoundedChannelFullMode"/> by using the
    /// <see cref="ChannelBasedSlackQueue{T}(Channel{T})"/> overload.
    /// </summary>
    /// <param name="capacity">Maximum number of buffered items. Must be &gt; 0.</param>
    public ChannelBasedSlackQueue(int capacity)
        : this(BuildBoundedChannel(capacity))
    {
    }

    /// <summary>
    /// Initializes a new queue backed by the supplied channel. Exposed
    /// primarily so tests can inject a channel with non-default options.
    /// </summary>
    public ChannelBasedSlackQueue(Channel<T> channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        this.channel = channel;
    }

    /// <summary>
    /// Buffers <paramref name="item"/>. For unbounded queues this completes
    /// synchronously; for bounded queues it waits while the channel is at
    /// capacity. Throws <see cref="OperationCanceledException"/> if
    /// <paramref name="ct"/> is cancelled before the item is accepted.
    /// </summary>
    public ValueTask EnqueueAsync(T item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return this.channel.Writer.WriteAsync(item, ct);
    }

    /// <summary>
    /// Asynchronously dequeues the next item, waiting if the queue is
    /// empty. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled while waiting (including the case
    /// where the token is already cancelled at the call site).
    /// </summary>
    public ValueTask<T> DequeueAsync(CancellationToken ct)
    {
        return this.channel.Reader.ReadAsync(ct);
    }

    /// <summary>
    /// Signals that no further items will be enqueued. Subsequent
    /// <see cref="DequeueAsync"/> calls drain remaining items and then
    /// throw <see cref="ChannelClosedException"/>. Idempotent.
    /// </summary>
    public void Complete()
    {
        this.channel.Writer.TryComplete();
    }

    private static Channel<T> BuildBoundedChannel(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Queue capacity must be positive.");
        }

        return Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }
}
