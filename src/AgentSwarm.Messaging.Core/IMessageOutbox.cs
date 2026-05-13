namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Durable outbound-message queue contract. Aligned with <c>architecture.md</c> §4.4 and
/// <c>implementation-plan.md</c> §1.2.
/// </summary>
/// <remarks>
/// The interface is intentionally minimal — only the four operations the retry engine and
/// dead-letter path need are exposed. Transient-failure retry scheduling (incrementing
/// <see cref="OutboxEntry.RetryCount"/>, setting <see cref="OutboxEntry.NextRetryAt"/>,
/// reverting status to <see cref="OutboxEntryStatuses.Pending"/>) is handled internally by
/// the concrete implementation between <see cref="DequeueAsync"/> and
/// <see cref="AcknowledgeAsync"/>/<see cref="DeadLetterAsync"/>.
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
    /// </summary>
    /// <param name="outboxEntryId">The entry's primary key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the status has been recorded.</returns>
    Task AcknowledgeAsync(string outboxEntryId, CancellationToken ct);

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
