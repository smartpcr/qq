using System.Threading.Channels;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Webhook;

/// <summary>
/// Singleton in-memory dispatch channel between the synchronous webhook
/// endpoint (Stage 2.4 producer) and the asynchronous
/// <c>InboundUpdateDispatcher</c> background consumer in the Worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a channel rather than <c>Task.Run</c>.</b> The endpoint must
/// return HTTP 200 quickly (architecture.md §5.1 invariant 1) but the
/// pipeline call that follows is itself awaitable work with bounded
/// concurrency requirements (architecture.md §10.4 burst analysis). A
/// raw <c>Task.Run</c> after the response disconnects the work from any
/// scope and produces unbounded concurrency under burst — two failure
/// modes the iter-1 review flagged. The channel pattern (a) bounds the
/// in-flight queue depth via <see cref="BoundedChannelOptions.Capacity"/>,
/// (b) lets the consumer create a fresh DI scope per item (so the
/// pipeline is invoked outside the request scope's disposal window),
/// and (c) drains gracefully on shutdown because the consumer reads
/// from <see cref="ChannelReader{T}.ReadAllAsync"/> with the host's
/// <see cref="System.Threading.CancellationToken"/>.
/// </para>
/// <para>
/// <b>Durable backstop.</b> Items dropped from this channel (capacity
/// exhaustion, host crash before drain) are NOT lost — the
/// <see cref="InboundUpdate"/> row is persisted by the endpoint BEFORE
/// the row id is written to the channel, so the Stage 2.4
/// <c>InboundRecoverySweep</c> picks up any orphan rows on the next
/// sweep interval (default 60 s).
/// </para>
/// <para>
/// <b>What flows on the wire.</b> Only the <see cref="long"/>
/// <see cref="InboundUpdate.UpdateId"/> — the consumer re-reads the
/// durable row from <see cref="IInboundUpdateStore"/> so it picks up
/// any status mutations the recovery sweep made and avoids drift
/// between the in-memory channel and the database of record.
/// </para>
/// </remarks>
public sealed class InboundUpdateChannel
{
    /// <summary>
    /// Default in-memory capacity. 1024 is a deliberate ceiling: a
    /// realistic burst (100 agents emitting alerts within 1 s per the
    /// performance requirement) is one order of magnitude below this,
    /// so a healthy host never trips the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Full-channel behavior is fast-ACK, not block.</b> The only
    /// in-tree caller — <c>TelegramWebhookEndpoint.HandleAsync</c> at
    /// <c>TelegramWebhookEndpoint.cs</c> ~line 182 — uses
    /// <see cref="ChannelWriter{T}.TryWrite"/>, which returns
    /// <c>false</c> immediately (it does NOT suspend the producer) when
    /// the bounded channel is at capacity OR has been completed. That
    /// fast-fail is intentional: the durable <see cref="InboundUpdate"/>
    /// row is already persisted by the time we enqueue, so the
    /// <c>InboundRecoverySweep</c> replays any rejected row on its next
    /// tick. This preserves the story's fast-ACK boundary (P95 send
    /// latency &lt; 2 s) and is more important than draining every
    /// burst through a single in-process channel.
    /// </para>
    /// <para>
    /// <b>Why <see cref="BoundedChannelFullMode.Wait"/> is still set.</b>
    /// <c>FullMode</c> only affects the semantics of
    /// <see cref="ChannelWriter{T}.WriteAsync"/> /
    /// <see cref="ChannelWriter{T}.WaitToWriteAsync"/> — neither of
    /// which the webhook endpoint calls. We keep <c>Wait</c> as the
    /// safe default for any future async caller (e.g. an internal
    /// long-polling shim in Stage 2.5): a hypothetical
    /// <c>WriteAsync</c> path would defer rather than silently drop,
    /// which is the correct behavior for a non-webhook producer that
    /// has not separately persisted the row. <c>TryWrite</c> is
    /// unaffected and remains the only documented fast-ACK enqueue
    /// path.
    /// </para>
    /// </remarks>
    public const int DefaultCapacity = 1024;

    private readonly Channel<long> _channel;

    public InboundUpdateChannel()
        : this(DefaultCapacity)
    {
    }

    public InboundUpdateChannel(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "must be positive.");
        }

        _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelWriter<long> Writer => _channel.Writer;

    public ChannelReader<long> Reader => _channel.Reader;
}
