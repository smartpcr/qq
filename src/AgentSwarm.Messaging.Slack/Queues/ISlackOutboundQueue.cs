using AgentSwarm.Messaging.Slack.Transport;

namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// Buffers rendered <see cref="SlackOutboundEnvelope"/> instances between
/// the <c>SlackConnector</c> (which renders Block Kit and resolves the
/// destination thread) and the <c>SlackOutboundDispatcher</c> background
/// service (which calls the Slack Web API). Provides at-least-once
/// delivery semantics for outbound messages and absorbs Slack rate-limit
/// pauses without back-pressuring the agent producer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 1.3 contract.</b> The in-process
/// <see cref="ChannelBasedSlackQueue{T}"/> backs this interface for
/// development and tests. In production the swap-in implementation is the
/// disk-backed <see cref="FileSystemSlackOutboundQueue"/> (Stage 6.3
/// iter 2): the agent-uploaded story attachment's FR-005 (durable
/// outbound queue, connector restart recovery) and FR-007 ("0 tolerated
/// message loss") require that an enqueued envelope survives a worker
/// restart -- the in-process channel queue does NOT meet that bar.
/// Durable backends additionally implement
/// <see cref="IAcknowledgeableSlackOutboundQueue"/> so the
/// <c>SlackOutboundDispatcher</c> can remove the journal entry only
/// after a TERMINAL disposition (delivered to Slack OR safely
/// dead-lettered).
/// </para>
/// <para>
/// <c>views.open</c> calls bypass this queue (see architecture.md section
/// 2.16 and 3.4) because they require the short-lived <c>trigger_id</c>
/// from a slash-command request; only the deferable
/// <see cref="SlackOutboundOperationKind.PostMessage"/>,
/// <see cref="SlackOutboundOperationKind.UpdateMessage"/>, and
/// <see cref="SlackOutboundOperationKind.ViewsUpdate"/> verbs are queued.
/// </para>
/// </remarks>
internal interface ISlackOutboundQueue
{
    /// <summary>
    /// Buffers a rendered outbound envelope. Implementations backed by a
    /// bounded queue MAY block while at capacity; the in-process
    /// <see cref="ChannelBasedSlackOutboundQueue"/> uses an unbounded
    /// channel and completes synchronously.
    /// </summary>
    /// <remarks>
    /// Signature matches the Stage 1.3 brief literally
    /// (<c>EnqueueAsync(SlackOutboundEnvelope)</c>) -- a cancellation token
    /// is intentionally NOT exposed here so the agent-side producer (which
    /// has already handed off ownership of the rendered message) cannot
    /// retract a queued send. Implementations that require per-call
    /// cancellation expose it on their concrete type, not on this
    /// interface.
    /// </remarks>
    ValueTask EnqueueAsync(SlackOutboundEnvelope envelope);

    /// <summary>
    /// Asynchronously dequeues the next envelope, waiting if the queue is
    /// empty. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled while waiting.
    /// </summary>
    ValueTask<SlackOutboundEnvelope> DequeueAsync(CancellationToken ct);
}
