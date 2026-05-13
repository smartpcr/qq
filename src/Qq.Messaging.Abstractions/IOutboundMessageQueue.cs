namespace Qq.Messaging.Abstractions;

/// <summary>
/// Durable outbound message queue with lease-based consumption,
/// deduplication, retry scheduling, and dead-letter support.
/// </summary>
public interface IOutboundMessageQueue
{
    /// <summary>Enqueue a message for delivery. Duplicates by DeduplicationKey are ignored.</summary>
    Task EnqueueAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>Enqueue a batch of messages atomically.</summary>
    Task EnqueueBatchAsync(
        IReadOnlyList<MessageEnvelope> envelopes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lease the next visible message for processing.
    /// Returns null when the queue is empty or all messages are leased.
    /// </summary>
    Task<QueueLease?> LeaseAsync(
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledge successful delivery; removes the message from the queue.</summary>
    Task AcknowledgeAsync(string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Release a lease and schedule the message for retry after <paramref name="visibilityDelay"/>.
    /// </summary>
    Task ReleaseAsync(
        string leaseToken,
        TimeSpan visibilityDelay,
        CancellationToken cancellationToken = default);

    /// <summary>Move a message to the dead-letter queue after exhausting retries.</summary>
    Task DeadLetterAsync(
        string leaseToken,
        string reason,
        CancellationToken cancellationToken = default);
}
