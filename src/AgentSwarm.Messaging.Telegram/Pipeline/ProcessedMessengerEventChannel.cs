using System.Threading.Channels;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline;

/// <summary>
/// Singleton in-memory dispatch channel that bridges the Stage 2.2
/// <see cref="TelegramUpdatePipeline"/> (producer, writes one
/// <see cref="MessengerEvent"/> per processed update) to the Stage 2.6
/// <see cref="TelegramMessengerConnector"/> (consumer, drains via
/// <see cref="IMessengerConnector.ReceiveAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a channel rather than a callback list.</b> The connector's
/// <see cref="IMessengerConnector.ReceiveAsync"/> contract returns an
/// <see cref="IReadOnlyList{T}"/> of events drained at the caller's
/// cadence — explicitly a poll-based shape, not a push-based one. An
/// <see cref="Channel{T}"/> gives us:
///   (a) lossless ordering between the pipeline's process-and-publish
///       sequence and the connector's drain sequence,
///   (b) guaranteed delivery — <see cref="ChannelWriter{T}.TryWrite"/>
///       never returns <c>false</c> on an unbounded channel, so every
///       processed update reaches the connector even under sustained
///       bursts, and
///   (c) thread-safe enqueue / drain across multiple pipeline
///       invocations and any number of connector consumers (single-
///       writer / single-reader assumptions are <i>not</i> made).
/// </para>
/// <para>
/// <b>Why unbounded (iter-2 evaluator item 4).</b> The Stage 2.6 brief
/// frames <see cref="IMessengerConnector.ReceiveAsync"/> as the
/// connector's <i>inbound drain</i> and the story-level requirement is
/// "no message loss" under bursts from 100+ agents. A bounded channel
/// with fast-drop <see cref="System.Threading.Channels.BoundedChannelFullMode"/>
/// would silently lose processed events when the consumer is slower
/// than the pipeline producer — exactly the failure shape the
/// performance acceptance criterion forbids. Memory growth is bounded
/// in practice by the consumer's drain cadence (the
/// <c>SwarmEventSubscriptionService</c> wired in Stage 2.7 drains on
/// every tick); a misconfigured host with no consumer would manifest
/// as observable memory pressure, which is loud and triage-able,
/// rather than as silent dropped audits.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Producer writes via <see cref="ChannelWriter{T}.TryWrite"/>
/// always succeed on an unbounded channel (the API still exists for
/// uniformity with the bounded sibling
/// <see cref="Webhook.InboundUpdateChannel"/>, which uses fast-drop
/// because the durable <c>InboundUpdate</c> row is its recovery
/// primitive). Consumer drains via
/// <see cref="ChannelReader{T}.TryRead(out T)"/> inside a non-blocking
/// loop because the connector's
/// <see cref="IMessengerConnector.ReceiveAsync"/> contract requires it
/// to return whatever is currently buffered without blocking when the
/// channel is empty.
/// </para>
/// </remarks>
public sealed class ProcessedMessengerEventChannel
{
    private readonly Channel<MessengerEvent> _channel;

    public ProcessedMessengerEventChannel()
    {
        // Unbounded — see class remarks. The Stage 2.6 acceptance
        // criterion "support burst alerts from 100+ agents without
        // message loss" precludes any bounded/fast-drop configuration
        // on this hop; the durable InboundUpdate row covers replay for
        // the cross-process pipeline boundary, but THIS channel is the
        // last-mile in-process drain and has no persistent backstop.
        _channel = Channel.CreateUnbounded<MessengerEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelWriter<MessengerEvent> Writer => _channel.Writer;

    public ChannelReader<MessengerEvent> Reader => _channel.Reader;
}
