namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core entity for the <c>OutboxMessages</c> table per <c>implementation-plan.md</c>
/// §6.1 column list and <c>architecture.md</c> §3.2 OutboxEntry field table. One row per
/// durable outbound delivery — the row remains in the table after dispatch (the audit
/// trail required by the story's Compliance pillar).
/// </summary>
public sealed class OutboxEntryEntity
{
    /// <summary>Primary key.</summary>
    public string OutboxEntryId { get; set; } = string.Empty;

    /// <summary>End-to-end trace ID.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Canonical destination URI.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>One of <c>Personal</c> or <c>Channel</c> per <c>OutboxDestinationTypes</c>.</summary>
    public string? DestinationType { get; set; }

    /// <summary>Bare target identifier.</summary>
    public string? DestinationId { get; set; }

    /// <summary>One of <c>OutboxPayloadTypes.All</c>.</summary>
    public string PayloadType { get; set; } = string.Empty;

    /// <summary>Serialized payload.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Serialized Bot Framework <c>ConversationReference</c> snapshot resolved at
    /// enqueue time. The dispatcher rehydrates this directly so no store lookup is
    /// required at retry time.
    /// </summary>
    public string? ConversationReferenceJson { get; set; }

    /// <summary>
    /// Teams ActivityId captured on the successful delivery. Persisted on the audit row
    /// itself so a downstream <c>ICardStateStore</c> failure does not lose the
    /// identifier.
    /// </summary>
    public string? ActivityId { get; set; }

    /// <summary>Teams ConversationId captured on the successful delivery.</summary>
    public string? ConversationId { get; set; }

    /// <summary>Current lifecycle state — one of <c>OutboxEntryStatuses.All</c>.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>Number of failed attempts.</summary>
    public int RetryCount { get; set; }

    /// <summary>Next eligible retry timestamp.</summary>
    public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>Last failure reason.</summary>
    public string? LastError { get; set; }

    /// <summary>Enqueue timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Successful delivery timestamp.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// Lease expiry stamped at dequeue. A worker that crashes between dequeue and
    /// ack/dead-letter leaves the row in <c>Processing</c> with a populated lease; the
    /// next dequeue selects <c>Pending</c> rows AND <c>Processing</c> rows whose lease
    /// has expired, satisfying the architecture's "0 message loss" invariant. Nulled by
    /// <c>AcknowledgeAsync</c> / <c>DeadLetterAsync</c> / <c>RescheduleAsync</c>.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
