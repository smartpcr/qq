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

    /// <summary>
    /// Target scope for the outbound send — one of <see cref="OutboxDestinationTypes.All"/>
    /// (<c>Personal</c> or <c>Channel</c>). Optional in the canonical
    /// <c>architecture.md</c> §3.2 schema where <see cref="Destination"/> already carries
    /// the scope inside the URI; populated explicitly by Stage 6.1 per
    /// <c>implementation-plan.md</c> §6.1 column list so delivery code can branch on the
    /// scope without re-parsing the URI.
    /// </summary>
    public string? DestinationType { get; init; }

    /// <summary>
    /// Bare target identifier (user ID for <see cref="OutboxDestinationTypes.Personal"/>;
    /// channel ID for <see cref="OutboxDestinationTypes.Channel"/>). Optional companion to
    /// <see cref="DestinationType"/> per <c>implementation-plan.md</c> §6.1.
    /// </summary>
    public string? DestinationId { get; init; }

    /// <summary>
    /// Serialized Bot Framework <c>ConversationReference</c> resolved from
    /// <c>IConversationReferenceStore</c> at enqueue time. The retry engine deserializes
    /// this field to rehydrate the proactive turn context via
    /// <c>CloudAdapter.ContinueConversationAsync</c> at delivery time — no additional
    /// store lookup is needed during retry. Optional so that non-Teams payloads or
    /// pre-Stage 6.1 entries remain valid.
    /// </summary>
    public string? ConversationReferenceJson { get; init; }

    /// <summary>
    /// Teams activity ID captured from <c>SendActivityAsync</c>'s <c>ResourceResponse.Id</c>
    /// after a successful delivery. Reserved for forward-looking observability /
    /// reconciliation; not populated until delivery completes.
    /// </summary>
    public string? ActivityId { get; init; }

    /// <summary>
    /// Bot Framework conversation ID captured from the proactive turn context after a
    /// successful delivery. Reserved for forward-looking observability / reconciliation;
    /// not populated until delivery completes.
    /// </summary>
    public string? ConversationId { get; init; }

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

    /// <summary>
    /// Optional lease expiry stamped onto the row at dequeue. A crashed worker that
    /// never acknowledges or dead-letters its claim leaves the row in
    /// <see cref="OutboxEntryStatuses.Processing"/>; the next dequeue selects rows
    /// whose <see cref="LeaseExpiresAt"/> is in the past so the entry is recovered
    /// (architecture.md §9 "0 message loss"). <c>null</c> for rows that have never
    /// been dequeued or have been acknowledged/dead-lettered.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }
}
