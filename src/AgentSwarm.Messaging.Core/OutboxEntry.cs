namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Durable outbound-message queue entry. Defined in <c>AgentSwarm.Messaging.Core</c> per
/// <c>architecture.md</c> §3.2. The retry/dead-letter engine and the
/// <see cref="IMessageOutbox"/> contract operate exclusively on this record.
/// </summary>
/// <remarks>
/// All public properties are init-only so an enqueued entry cannot be mutated by callers
/// after construction. Status transitions are performed by replacing the record via
/// <c>with</c> expressions inside the concrete <see cref="IMessageOutbox"/> implementation.
/// </remarks>
public sealed record OutboxEntry
{
    /// <summary>Primary key (typically a GUID).</summary>
    public required string OutboxEntryId { get; init; }

    /// <summary>End-to-end trace ID inherited from the originating task/question/message.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Serialized routing key in the canonical URI shape
    /// <c>teams://{tenantId}/user/{userId}</c> for personal delivery or
    /// <c>teams://{tenantId}/channel/{channelId}</c> for channel delivery. Aligned with
    /// <c>architecture.md</c> §3.1 routing-derivation note.
    /// </summary>
    public required string Destination { get; init; }

    /// <summary>Discriminator: one of <see cref="OutboxPayloadTypes.All"/>.</summary>
    public required string PayloadType { get; init; }

    /// <summary>Serialized payload (JSON) — the original
    /// <see cref="AgentSwarm.Messaging.Abstractions.MessengerMessage"/> or
    /// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/>.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>One of <see cref="OutboxEntryStatuses.All"/>. Defaults to
    /// <see cref="OutboxEntryStatuses.Pending"/> for a newly enqueued entry.</summary>
    public string Status { get; init; } = OutboxEntryStatuses.Pending;

    /// <summary>Delivery attempt count (incremented on each retry by
    /// <c>OutboxRetryEngine</c>).</summary>
    public int RetryCount { get; init; }

    /// <summary>Scheduled next attempt time. <c>null</c> until the first failure schedules a
    /// retry.</summary>
    public DateTimeOffset? NextRetryAt { get; init; }

    /// <summary>Last failure reason captured by the retry engine. <c>null</c> when no
    /// failures have occurred.</summary>
    public string? LastError { get; init; }

    /// <summary>Enqueue time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Successful delivery time. <c>null</c> until status transitions to
    /// <see cref="OutboxEntryStatuses.Sent"/>.</summary>
    public DateTimeOffset? DeliveredAt { get; init; }
}
