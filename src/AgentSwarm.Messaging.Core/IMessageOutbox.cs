namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Durable outbound-message queue contract. Aligned with <c>architecture.md</c> §4.4 and
/// <c>implementation-plan.md</c> §1.2.
/// </summary>
/// <remarks>
/// The interface exposes five operations the retry engine drives explicitly:
/// <see cref="EnqueueAsync"/>, <see cref="DequeueAsync"/>, <see cref="AcknowledgeAsync"/>,
/// <see cref="RescheduleAsync"/>, and <see cref="DeadLetterAsync"/>. The engine
/// (Stage 6.1 <c>OutboxRetryEngine</c>) is the single component that decides — based on
/// the failure type and the current retry count — whether to acknowledge, reschedule, or
/// dead-letter an in-flight entry. The outbox is a passive store; it does not interpret
/// failure semantics.
/// </remarks>
public interface IMessageOutbox
{
    /// <summary>
    /// Persist a new <see cref="OutboxEntry"/> with <c>Status =
    /// </c><see cref="OutboxEntryStatuses.Pending"/>.
    /// </summary>
    /// <param name="entry">The entry to enqueue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the row has been persisted.</returns>
    Task EnqueueAsync(OutboxEntry entry, CancellationToken ct);

    /// <summary>
    /// Atomically select up to <paramref name="batchSize"/> entries with <c>Status =
    /// </c><see cref="OutboxEntryStatuses.Pending"/> and transition them to
    /// <see cref="OutboxEntryStatuses.Processing"/>. The returned entries are the lease
    /// the caller must acknowledge or dead-letter.
    /// </summary>
    /// <param name="batchSize">Maximum number of entries to dequeue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list (possibly empty) of dequeued entries.</returns>
    Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct);

    /// <summary>
    /// Mark a successfully delivered entry as <see cref="OutboxEntryStatuses.Sent"/>.
    /// Persists the supplied <see cref="OutboxDeliveryReceipt"/> identifiers
    /// (<see cref="OutboxEntry.ActivityId"/>, <see cref="OutboxEntry.ConversationId"/>,
    /// <see cref="OutboxEntry.DeliveredAt"/>) on the row so the audit trail is
    /// self-contained — downstream stores (<c>ICardStateStore</c>,
    /// <c>IAgentQuestionStore</c>) may also receive copies of the same identifiers, but
    /// the outbox row remains the authoritative record of what was delivered when.
    /// </summary>
    /// <param name="outboxEntryId">The entry's primary key.</param>
    /// <param name="receipt">Identifiers and timestamp captured by the dispatcher.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the status has been recorded.</returns>
    Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct);

    /// <summary>
    /// Persist the <see cref="OutboxDeliveryReceipt"/> identifiers on a row that is
    /// still in <see cref="OutboxEntryStatuses.Processing"/> — without changing the
    /// status. Used by an <c>IOutboxDispatcher</c> that has completed the underlying
    /// transport send (e.g. Bot Framework returned an <c>ActivityId</c>) but has not
    /// yet finished post-send persistence (card-state save, question conversation-id
    /// update). Stamping the receipt on the row at this point lets a retry observe
    /// "BF send already succeeded" and short-circuit the re-send so a downstream
    /// persistence failure does not cause a duplicate card.
    /// </summary>
    /// <param name="outboxEntryId">The entry's primary key.</param>
    /// <param name="receipt">Identifiers and timestamp captured by the dispatcher.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the receipt has been recorded.</returns>
    Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct);

    /// <summary>
    /// Reschedule a transiently failed entry: increment <see cref="OutboxEntry.RetryCount"/>,
    /// reset <see cref="OutboxEntry.Status"/> to <see cref="OutboxEntryStatuses.Pending"/>,
    /// stamp <see cref="OutboxEntry.NextRetryAt"/> with <paramref name="nextRetryAt"/>,
    /// capture <paramref name="error"/> on <see cref="OutboxEntry.LastError"/>, and clear
    /// the lease so a different worker may claim the row on the next dequeue.
    /// </summary>
    /// <param name="outboxEntryId">The entry's primary key.</param>
    /// <param name="nextRetryAt">When the entry becomes eligible for redelivery.</param>
    /// <param name="error">Failure reason captured on the row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the row has been updated.</returns>
    Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct);

    /// <summary>
    /// Mark a permanently failed entry as <see cref="OutboxEntryStatuses.DeadLettered"/>
    /// after exhausting retries, capturing the supplied <paramref name="error"/> on the row.
    /// </summary>
    /// <param name="outboxEntryId">The entry's primary key.</param>
    /// <param name="error">Failure reason to record on the row's <see cref="OutboxEntry.LastError"/> field.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the status has been recorded.</returns>
    Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct);
}
