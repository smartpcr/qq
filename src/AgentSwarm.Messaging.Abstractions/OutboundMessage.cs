namespace AgentSwarm.Messaging.Abstractions;

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
/// <remarks>
/// <para>
/// <b>Assembly placement.</b> Lives in <c>AgentSwarm.Messaging.Abstractions</c>
/// rather than <c>Core</c> so that <see cref="IOutboundQueue"/> — which the
/// Stage 1.4 brief explicitly places in Abstractions — can reference it
/// without forcing a forbidden <c>Abstractions → Core</c> project
/// reference. The record's only non-primitive dependencies
/// (<see cref="MessageSeverity"/>, <see cref="CorrelationIdValidation"/>,
/// <see cref="AgentQuestionEnvelope"/>) are all already in Abstractions, so
/// the relocation introduces no new cross-assembly references.
/// </para>
/// <para>
/// <see cref="CorrelationId"/> is guarded by
/// <see cref="CorrelationIdValidation.Require"/> at construction time —
/// the "All messages include trace/correlation ID" acceptance criterion
/// applies uniformly to every outbound record, not only inbound /
/// transport-facing ones; an outbox row with an empty trace id would
/// silently drop the trace at the send boundary.
/// </para>
/// </remarks>
public sealed record OutboundMessage
{
    private readonly string _correlationId = null!;

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
    /// Serialized original source envelope, preserved verbatim for recovery,
    /// dead-letter replay, and (for questions) <c>QuestionRecoverySweep</c>
    /// backfill of <c>PendingQuestionRecord</c> fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per architecture.md §3.1, this field is populated for two
    /// <see cref="OutboundSourceType"/> values and null for the other two:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="OutboundSourceType.Question"/>: full
    /// <see cref="AgentQuestionEnvelope"/> JSON. Read by
    /// <c>TelegramMessageSender.SendQuestionAsync</c> to render the inline
    /// keyboard / MarkdownV2 text at send time, and by
    /// <c>QuestionRecoverySweep</c> to reconstruct
    /// <c>PendingQuestionRecord</c> after a crash between
    /// <c>MarkSentAsync</c> and store persistence (architecture.md §3.1
    /// Gap B).
    /// </description></item>
    /// <item><description>
    /// <see cref="OutboundSourceType.Alert"/>: full <c>AgentAlertEvent</c>
    /// JSON (or, at the <c>IMessengerConnector</c> boundary where the
    /// raw <c>SwarmEvent</c> type has already been projected to
    /// <see cref="MessengerMessage"/>, the serialized
    /// <see cref="MessengerMessage"/> alert payload). Preserved so that
    /// dead-letter replay and audit can reconstruct the original alert
    /// without re-querying the swarm event source.
    /// </description></item>
    /// <item><description>
    /// <see cref="OutboundSourceType.StatusUpdate"/> and
    /// <see cref="OutboundSourceType.CommandAck"/>: always <c>null</c>.
    /// These message types are self-describing through
    /// <see cref="Payload"/> and have no upstream envelope to preserve.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// When the outbound message is dead-lettered, the contents of this
    /// field are copied verbatim to
    /// <c>DeadLetterMessage.SourceEnvelopeJson</c> (distinct from
    /// <c>DeadLetterMessage.Payload</c>, which holds the rendered
    /// Telegram content).
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Trace identifier — must be non-null, non-empty, non-whitespace per
    /// the "All messages include trace/correlation ID" acceptance criterion.
    /// Validated via <see cref="CorrelationIdValidation.Require"/> at
    /// construction time so an outbox row cannot reach the send boundary
    /// with a missing trace.
    /// </summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    public string? ErrorDetail { get; init; }
}
