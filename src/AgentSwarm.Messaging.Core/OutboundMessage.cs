namespace AgentSwarm.Messaging.Core;

using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Discriminator for outbound message origin type.
/// </summary>
public enum OutboundSourceType
{
    Question,
    Alert,
    StatusUpdate,
    CommandAck
}

/// <summary>
/// Delivery status of an outbound message.
/// </summary>
public enum OutboundMessageStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
    DeadLettered
}

/// <summary>
/// Durable outbox record for outbound Telegram messages.
/// Matches architecture.md §3.1 OutboundMessage data model.
/// </summary>
public sealed record OutboundMessage
{
    /// <summary>Internal unique identifier. Primary key.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>
    /// Deterministic key preventing duplicate sends.
    /// Derivation depends on SourceType — see architecture.md idempotency key derivation table.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Target Telegram chat.</summary>
    public required long ChatId { get; init; }

    /// <summary>Serialized MessengerMessage or AgentQuestion payload.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Full serialized <see cref="AgentQuestionEnvelope"/> JSON for question-type messages.
    /// Preserved for recovery/backfill so the connector can reconstruct the original envelope
    /// without re-querying the source. Null for non-question source types.
    /// </summary>
    public string? SourceEnvelopeJson { get; init; }

    public required MessageSeverity Severity { get; init; }

    public required OutboundSourceType SourceType { get; init; }

    public string? SourceId { get; init; }

    public OutboundMessageStatus Status { get; init; } = OutboundMessageStatus.Pending;

    public int AttemptCount { get; init; }

    public int MaxAttempts { get; init; } = 5;

    public DateTimeOffset? NextRetryAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    /// <summary>
    /// Telegram's returned message_id on success.
    /// Canonical type is long (nullable before first successful send).
    /// </summary>
    public long? TelegramMessageId { get; init; }

    public required string CorrelationId { get; init; }

    public string? ErrorDetail { get; init; }
}
