// -----------------------------------------------------------------------
// <copyright file="ISlackInboundQueue.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentSwarm.Messaging.Slack.Transport;

namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// Buffers signature-verified inbound <see cref="SlackInboundEnvelope"/>
/// instances between the Slack transport layer (Events API receiver,
/// slash-command receiver, interactions receiver, Socket Mode receiver)
/// and the <c>SlackInboundIngestor</c> background service. The transport
/// layer ACKs Slack immediately (HTTP 200 within Slack's 3-second budget)
/// and then enqueues the envelope here so processing can proceed
/// asynchronously without blocking the HTTP request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 1.3 contract.</b> The in-process
/// <see cref="ChannelBasedSlackQueue{T}"/> backs this interface for
/// development and tests. In production, the swap-in implementation will
/// be a durable queue (database-backed outbox/inbox, Service Bus, etc.)
/// supplied by the upstream <c>AgentSwarm.Messaging.Core</c> project per
/// Stage 1.3 of <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </para>
/// <para>
/// Implementations MUST preserve FIFO order across a single producer and
/// SHOULD honour FIFO across concurrent producers as a best-effort
/// guarantee.
/// </para>
/// </remarks>
internal interface ISlackInboundQueue
{
    /// <summary>
    /// Buffers a normalized inbound envelope. Implementations backed by a
    /// bounded queue MAY block while at capacity; the in-process
    /// <see cref="ChannelBasedSlackInboundQueue"/> uses an unbounded
    /// channel and completes synchronously.
    /// </summary>
    /// <remarks>
    /// Signature matches the Stage 1.3 brief literally
    /// (<c>EnqueueAsync(SlackInboundEnvelope)</c>) -- a cancellation token
    /// is intentionally NOT exposed here so the transport-layer ACK path
    /// (which has already returned HTTP 200 to Slack) cannot be cancelled
    /// after the fact. Implementations that require per-call cancellation
    /// expose it on their concrete type, not on this interface.
    /// </remarks>
    ValueTask EnqueueAsync(SlackInboundEnvelope envelope);

    /// <summary>
    /// Asynchronously dequeues the next envelope, waiting if the queue is
    /// empty. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled while waiting.
    /// </summary>
    ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct);
}
