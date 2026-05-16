namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Durable, priority-aware outbound queue contract. Persists every outbound
/// message before delivery so the system can survive process crashes, retry
/// transient platform failures, and dead-letter exhausted attempts. See
/// architecture.md Section 4.4 (interface) and Section 10.3 (durability) and
/// Section 10.4 (rate-limit-aware batching for low-priority traffic).
/// </summary>
/// <remarks>
/// Implementations enforce the UNIQUE constraint on
/// <see cref="OutboundMessage.IdempotencyKey"/> in <see cref="EnqueueAsync"/>
/// so duplicate producer requests for the same logical event collapse to a
/// single row. <see cref="DequeueAsync"/> returns the highest-severity pending
/// message oldest-first within each severity band; transactional pickup
/// (<see cref="OutboundMessageStatus.Pending"/> → <see cref="OutboundMessageStatus.Sending"/>)
/// is the implementation's responsibility.
/// </remarks>
public interface IOutboundQueue
{
    /// <summary>
    /// Persists <paramref name="message"/>. Implementations must enforce the
    /// idempotency contract: if a row with the same
    /// <see cref="OutboundMessage.IdempotencyKey"/> already exists, the call
    /// must be a no-op (or logically merge) rather than create a duplicate.
    /// </summary>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnqueueAsync(OutboundMessage message, CancellationToken ct);

    /// <summary>
    /// Pops the next message to dispatch. Returns the highest-severity
    /// <see cref="OutboundMessageStatus.Pending"/> message (Critical first,
    /// Low last), oldest-first within each severity. Failed messages whose
    /// <see cref="OutboundMessage.NextRetryAt"/> has elapsed are also eligible.
    /// Returns <see langword="null"/> when nothing is currently due.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<OutboundMessage?> DequeueAsync(CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="messageId"/> as
    /// <see cref="OutboundMessageStatus.Sent"/>, recording the platform-side id
    /// for audit/correlation.
    /// </summary>
    /// <param name="messageId">Outbound message id.</param>
    /// <param name="platformMessageId">Platform identifier returned by the API (Discord message snowflake cast to <see cref="long"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkSentAsync(Guid messageId, long platformMessageId, CancellationToken ct);

    /// <summary>
    /// Records a failed dispatch attempt: increments
    /// <see cref="OutboundMessage.AttemptCount"/>, captures
    /// <paramref name="error"/> in <see cref="OutboundMessage.ErrorDetail"/>,
    /// and computes <see cref="OutboundMessage.NextRetryAt"/> via the
    /// implementation's backoff schedule. When the post-increment count reaches
    /// <see cref="OutboundMessage.MaxAttempts"/>, the implementation must
    /// transition the row to <see cref="OutboundMessageStatus.DeadLettered"/>
    /// via <see cref="DeadLetterAsync"/>.
    /// </summary>
    /// <param name="messageId">Outbound message id.</param>
    /// <param name="error">Failure reason text suitable for operator triage.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct);

    /// <summary>
    /// Forces transition to <see cref="OutboundMessageStatus.DeadLettered"/>.
    /// The OutboundMessage row is retained (operators may manually requeue) and
    /// a linked <c>DeadLetterMessage</c> record is created with the full error
    /// history per architecture.md Section 3.2.
    /// </summary>
    /// <param name="messageId">Outbound message id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeadLetterAsync(Guid messageId, CancellationToken ct);

    /// <summary>
    /// Counts <see cref="OutboundMessageStatus.Pending"/> messages of a given
    /// severity. Used by the outbound dispatcher to decide whether the
    /// rate-limit-aware batching threshold for low-priority traffic is met
    /// (architecture.md Section 10.4).
    /// </summary>
    /// <param name="severity">Severity to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountPendingAsync(MessageSeverity severity, CancellationToken ct);

    /// <summary>
    /// Pops up to <paramref name="maxCount"/> pending messages of a given
    /// severity, oldest-first. Used for batched send of low-priority status
    /// updates (architecture.md Section 10.4) — Critical/High traffic is sent
    /// individually via <see cref="DequeueAsync"/>. Returns an empty list
    /// (never <see langword="null"/>) when nothing is available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned batch may span multiple <see cref="OutboundMessage.ChatId"/>
    /// values and multiple <see cref="OutboundMessage.SourceType"/>s — the
    /// queue contract intentionally filters on severity only (per the
    /// implementation-plan signature). Callers that hand the batch to
    /// <see cref="IMessageSender.SendBatchAsync"/> (which is single-channel
    /// scoped) must group the result by <see cref="OutboundMessage.ChatId"/>
    /// and filter to <see cref="OutboundMessageSource.StatusUpdate"/> rows
    /// before invoking the sender.
    /// </para>
    /// <para>
    /// Implementations must atomically claim returned rows using the same
    /// transactional pickup as <see cref="DequeueAsync"/> so concurrent
    /// dispatchers cannot dequeue the same row twice.
    /// </para>
    /// </remarks>
    /// <param name="severity">Severity to drain.</param>
    /// <param name="maxCount">Cap on the batch size returned.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<OutboundMessage>> DequeueBatchAsync(
        MessageSeverity severity,
        int maxCount,
        CancellationToken ct);
}
