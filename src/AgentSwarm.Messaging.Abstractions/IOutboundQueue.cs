namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Durable outbound message queue used by the Telegram connector
/// (Stage 2.6) to enqueue messages and by <c>OutboundQueueProcessor</c>
/// (Stage 4.1) to dequeue, mark-sent, mark-failed, and dead-letter them.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>AgentSwarm.Messaging.Abstractions</c> per
/// implementation-plan.md Stage 1.4 line 98 and architecture.md §4.4.
/// Both <see cref="IOutboundQueue"/> and <see cref="OutboundMessage"/>
/// live in Abstractions so that <c>TelegramMessengerConnector</c>
/// (Stage 2.6, in <c>AgentSwarm.Messaging.Telegram</c>) can delegate
/// sends without taking a project reference on the concrete
/// implementation (Stage 4.1).
/// </para>
/// <para>
/// All <see cref="Guid"/> <c>messageId</c> arguments correspond to
/// <see cref="OutboundMessage.MessageId"/>; the <see cref="long"/>
/// <c>telegramMessageId</c> on <see cref="MarkSentAsync"/> follows
/// architecture.md §3.1's canonical-type convention for Telegram
/// identifiers (which are <c>int64</c> on the wire).
/// </para>
/// </remarks>
public interface IOutboundQueue
{
    /// <summary>
    /// Persist a new outbound message in the queue with status
    /// <c>Pending</c>.
    /// </summary>
    Task EnqueueAsync(OutboundMessage message, CancellationToken ct);

    /// <summary>
    /// Atomically claim the next message for delivery, returning the
    /// highest-severity (<c>Critical &gt; High &gt; Normal &gt; Low</c>)
    /// pending message — and within a severity, oldest first. Returns
    /// <c>null</c> when the queue has no pending work; the caller is
    /// expected to back off and retry.
    /// </summary>
    Task<OutboundMessage?> DequeueAsync(CancellationToken ct);

    /// <summary>
    /// Record successful delivery: transitions the row to <c>Sent</c>
    /// and stamps it with the <paramref name="telegramMessageId"/>
    /// returned by the Telegram Bot API.
    /// </summary>
    Task MarkSentAsync(Guid messageId, long telegramMessageId, CancellationToken ct);

    /// <summary>
    /// Record a transient send failure: increments
    /// <see cref="OutboundMessage.AttemptCount"/>, stores
    /// <paramref name="error"/> on <see cref="OutboundMessage.ErrorDetail"/>,
    /// and re-schedules the row for retry until
    /// <see cref="OutboundMessage.MaxAttempts"/> is reached.
    /// </summary>
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct);

    /// <summary>
    /// Move a message that has exhausted its retry budget — or hit a
    /// permanent failure — to the dead-letter queue (status
    /// <c>DeadLettered</c>); the caller is expected to fan out to
    /// <see cref="IAlertService.SendAlertAsync"/> on a secondary
    /// channel.
    /// </summary>
    /// <param name="messageId">
    /// The <see cref="OutboundMessage.MessageId"/> of the row to
    /// dead-letter.
    /// </param>
    /// <param name="reason">
    /// <b>Stage 4.1 iter-2 evaluator item 5.</b> Human- and
    /// machine-readable failure reason persisted to
    /// <see cref="OutboundMessage.ErrorDetail"/> so the dead-letter
    /// transition does NOT lose the original failure category /
    /// message. The Stage 4.1 <c>OutboundQueueProcessor</c> passes a
    /// stringified <see cref="OutboundFailureCategory"/> plus the
    /// last error message; the in-line backpressure DLQ path inside
    /// <see cref="EnqueueAsync"/> passes the canonical
    /// <c>backpressure:queue_depth_exceeded</c> reason per
    /// architecture.md §10.4. Implementations MUST persist the
    /// reason and MUST increment
    /// <see cref="OutboundMessage.AttemptCount"/> by one so the audit
    /// trail records the final failure attempt rather than freezing
    /// the count at the last successfully-recorded
    /// <see cref="MarkFailedAsync"/> attempt.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task DeadLetterAsync(Guid messageId, string reason, CancellationToken ct);
}
