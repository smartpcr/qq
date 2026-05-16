// -----------------------------------------------------------------------
// <copyright file="IAcknowledgeableSlackOutboundQueue.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Queues;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Extends <see cref="ISlackOutboundQueue"/> with an explicit
/// per-envelope acknowledgement step so durable implementations can
/// retain envelopes in a write-ahead journal until the downstream
/// dispatcher confirms successful delivery (or successful
/// dead-lettering). Required to satisfy the story attachment's
/// FR-005 / FR-007 zero-message-loss guarantee across connector
/// restarts.
/// </summary>
/// <remarks>
/// <para>
/// The in-process <see cref="ChannelBasedSlackOutboundQueue"/> does
/// NOT implement this interface -- it is intended for dev / unit
/// tests where messages are lost on process exit anyway. Production
/// hosts wire <see cref="FileSystemSlackOutboundQueue"/> (or a
/// host-supplied database-backed equivalent) which implements this
/// contract so the
/// <c>SlackOutboundDispatcher</c> background service knows to
/// invoke <see cref="AcknowledgeAsync"/> after a terminal disposition
/// (success or successful DLQ enqueue). The dispatcher discovers
/// the capability via DI (downcast); queues that don't implement it
/// are unaffected.
/// </para>
/// <para>
/// <b>At-least-once semantics.</b> The dispatcher must call
/// <see cref="AcknowledgeAsync"/> only after the envelope's final
/// disposition is recorded somewhere durable -- the Slack Web API
/// returned 200, OR the dead-letter queue successfully captured the
/// envelope. If the dispatcher crashes between dequeue and ack, the
/// next process restart replays the un-acked envelope from the
/// journal. Duplicate delivery is acceptable per Slack's own
/// idempotency model; message loss is not.
/// </para>
/// </remarks>
internal interface IAcknowledgeableSlackOutboundQueue : ISlackOutboundQueue
{
    /// <summary>
    /// Acknowledges that <paramref name="envelope"/> reached a
    /// terminal disposition (delivered to Slack OR safely
    /// dead-lettered). Implementations remove the corresponding
    /// journal entry so it is not replayed on restart.
    /// </summary>
    /// <param name="envelope">The envelope previously returned from <see cref="ISlackOutboundQueue.DequeueAsync"/>.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    /// <remarks>
    /// Implementations match the supplied envelope to the original
    /// journal entry by <see cref="SlackOutboundEnvelope.EnvelopeId"/>
    /// (the stable per-envelope <see cref="System.Guid"/> assigned at
    /// enqueue time and persisted in the journal record). Calling
    /// <see cref="AcknowledgeAsync"/> with an envelope id the queue
    /// did not produce (or one already acked) is a no-op.
    /// </remarks>
    Task AcknowledgeAsync(SlackOutboundEnvelope envelope, CancellationToken ct = default);
}
