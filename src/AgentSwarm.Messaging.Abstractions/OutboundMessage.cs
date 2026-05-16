namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Durable outbound envelope persisted by every connector before delivery to its
/// platform API. Carries the rendered (or rendering-ready) payload, idempotency
/// key, retry/backoff bookkeeping, and the link back to the source event for
/// audit and replay. See architecture.md Section 3.2 (entity definition) and
/// Section 10.3 (durability semantics).
/// </summary>
/// <param name="MessageId">Surrogate primary key.</param>
/// <param name="IdempotencyKey">
/// Deterministic key derived from <see cref="SourceType"/> and the source
/// identifiers. The persistence store enforces a UNIQUE constraint on this
/// column so duplicate enqueues for the same logical event collapse into a
/// single row. Compute via the per-source factories on <see cref="IdempotencyKeys"/>
/// (e.g. <see cref="IdempotencyKeys.ForQuestion"/>) — the formulas are pinned
/// by architecture.md Section 3.2 and differ per source type.
/// </param>
/// <param name="ChatId">
/// Connector-native channel identifier. For Discord this is the channel
/// snowflake cast to <see cref="long"/> (snowflakes fit in an unsigned 64-bit
/// integer; for SQLite/EF interop we pin to signed <see cref="long"/> and
/// reinterpret on read). Other connectors carry the equivalent stringified id
/// (e.g. via <see cref="long.Parse(string)"/>) when they have a numeric channel
/// space, or hash a string id deterministically.
/// </param>
/// <param name="Severity">Drives priority-aware dequeue order in <see cref="IOutboundQueue"/>.</param>
/// <param name="Status">Lifecycle state. See <see cref="OutboundMessageStatus"/>.</param>
/// <param name="SourceType">Originating event class. See <see cref="OutboundMessageSource"/>.</param>
/// <param name="Payload">
/// Pre-rendered platform payload (e.g. Discord embed JSON for alerts and status
/// updates). For <see cref="OutboundMessageSource.Question"/> rows this is
/// typically a short fallback text; the full envelope lives in
/// <see cref="SourceEnvelopeJson"/> so the sender can re-render component shells
/// on retry without losing button/select-menu context.
/// </param>
/// <param name="SourceEnvelopeJson">
/// Optional serialized <see cref="AgentQuestionEnvelope"/> (or other source
/// envelope) used by the connector to re-render the message at send time. Null
/// for source types that fully render into <see cref="Payload"/>.
/// </param>
/// <param name="SourceId">
/// Free-form stable identifier of the originating event (<see cref="AgentQuestion.QuestionId"/>,
/// alert id, status update id, command id). Combined with the agent identifier
/// to compute <see cref="IdempotencyKey"/>.
/// </param>
/// <param name="AttemptCount">Number of dispatch attempts that have been made.</param>
/// <param name="MaxAttempts">
/// Inclusive cap on retry attempts before the message is dead-lettered.
/// Defaults to <see cref="DefaultMaxAttempts"/>.
/// </param>
/// <param name="NextRetryAt">
/// When to attempt the next dispatch. <see langword="null"/> for messages still
/// in <see cref="OutboundMessageStatus.Pending"/> on first attempt; populated by
/// <see cref="IOutboundQueue.MarkFailedAsync"/> with an exponential backoff.
/// </param>
/// <param name="PlatformMessageId">
/// Platform-side identifier returned on successful send (Discord message
/// snowflake cast to <see cref="long"/>). <see langword="null"/> until
/// <see cref="OutboundMessageStatus.Sent"/>.
/// </param>
/// <param name="CorrelationId">End-to-end trace identifier propagated from the source event.</param>
/// <param name="CreatedAt">When the message was enqueued.</param>
/// <param name="SentAt">When the message reached <see cref="OutboundMessageStatus.Sent"/>.</param>
/// <param name="ErrorDetail">
/// Last failure reason captured by <see cref="IOutboundQueue.MarkFailedAsync"/>.
/// Carried into <see cref="OutboundMessageStatus.DeadLettered"/> rows so operators
/// can triage without joining to a separate error log.
/// </param>
public sealed record OutboundMessage(
    Guid MessageId,
    string IdempotencyKey,
    long ChatId,
    MessageSeverity Severity,
    OutboundMessageStatus Status,
    OutboundMessageSource SourceType,
    string Payload,
    string? SourceEnvelopeJson,
    string? SourceId,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset? NextRetryAt,
    long? PlatformMessageId,
    string CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    string? ErrorDetail)
{
    /// <summary>
    /// Default <see cref="MaxAttempts"/> applied when producers do not override
    /// the retry budget. See architecture.md Section 10.3 (Outbound Durability).
    /// </summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>
    /// Creates a freshly enqueued <see cref="OutboundMessage"/> with sensible
    /// defaults for every field that has one — most importantly applying the
    /// pinned <see cref="DefaultMaxAttempts"/> (5) for <see cref="MaxAttempts"/>
    /// per the Stage 1.3 implementation-plan requirement and architecture.md
    /// Section 10.3. This is the preferred construction path for producers
    /// (envelope dispatchers, alerters, status-update writers); the positional
    /// constructor remains available for persistence-layer reconstitution where
    /// every column is restored verbatim.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Deterministic key — compute via <see cref="IdempotencyKeys"/>.
    /// </param>
    /// <param name="chatId">Connector-native channel identifier.</param>
    /// <param name="severity">Drives priority-aware dequeue order.</param>
    /// <param name="sourceType">Originating event class.</param>
    /// <param name="payload">Pre-rendered platform payload.</param>
    /// <param name="correlationId">End-to-end trace identifier.</param>
    /// <param name="sourceEnvelopeJson">Optional serialized source envelope.</param>
    /// <param name="sourceId">Optional free-form stable id of the source event.</param>
    /// <param name="maxAttempts">
    /// Inclusive cap on retry attempts. Defaults to <see cref="DefaultMaxAttempts"/>
    /// when the caller does not specify — this is the spec-mandated default.
    /// </param>
    /// <param name="messageId">
    /// Surrogate primary key. Defaults to a fresh <see cref="Guid.NewGuid"/>.
    /// </param>
    /// <param name="createdAt">
    /// Enqueue timestamp. Defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    /// <returns>
    /// A new <see cref="OutboundMessageStatus.Pending"/> envelope with
    /// <see cref="AttemptCount"/> 0, no <see cref="NextRetryAt"/>, no
    /// <see cref="PlatformMessageId"/>, no <see cref="SentAt"/>, and no
    /// <see cref="ErrorDetail"/>.
    /// </returns>
    public static OutboundMessage Create(
        string idempotencyKey,
        long chatId,
        MessageSeverity severity,
        OutboundMessageSource sourceType,
        string payload,
        string correlationId,
        string? sourceEnvelopeJson = null,
        string? sourceId = null,
        int maxAttempts = DefaultMaxAttempts,
        Guid? messageId = null,
        DateTimeOffset? createdAt = null)
    {
        return new OutboundMessage(
            MessageId: messageId ?? Guid.NewGuid(),
            IdempotencyKey: idempotencyKey,
            ChatId: chatId,
            Severity: severity,
            Status: OutboundMessageStatus.Pending,
            SourceType: sourceType,
            Payload: payload,
            SourceEnvelopeJson: sourceEnvelopeJson,
            SourceId: sourceId,
            AttemptCount: 0,
            MaxAttempts: maxAttempts,
            NextRetryAt: null,
            PlatformMessageId: null,
            CorrelationId: correlationId,
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            SentAt: null,
            ErrorDetail: null);
    }

    /// <summary>
    /// Deterministic <see cref="IdempotencyKey"/> factories for each
    /// <see cref="OutboundMessageSource"/>. The shapes are pinned by
    /// architecture.md Section 3.2 (Idempotency key derivation table) so the
    /// persistence-layer UNIQUE constraint collapses duplicate enqueues to a
    /// single row across the entire fleet:
    /// <list type="bullet">
    ///   <item><description><c>Question</c> → <c>q:{AgentId}:{QuestionId}</c></description></item>
    ///   <item><description><c>Alert</c> → <c>alert:{AgentId}:{AlertId}</c></description></item>
    ///   <item><description><c>StatusUpdate</c> → <c>s:{AgentId}:{CorrelationId}</c></description></item>
    ///   <item><description><c>CommandAck</c> → <c>c:{CorrelationId}</c> (no agent component)</description></item>
    /// </list>
    /// Use the explicit per-source factories below; they enforce the correct
    /// segment count for each source type (CommandAck has no
    /// <c>{AgentId}</c> segment).
    /// </summary>
    public static class IdempotencyKeys
    {
        /// <summary>
        /// Computes the idempotency key for an <see cref="OutboundMessageSource.Question"/>.
        /// Format: <c>q:{agentId}:{questionId}</c>. Example: a question from
        /// <c>build-agent-3</c> with question id <c>Q-42</c> yields
        /// <c>q:build-agent-3:Q-42</c>.
        /// </summary>
        public static string ForQuestion(string agentId, string questionId)
        {
            ValidateKeySegment(agentId, nameof(agentId));
            ValidateKeySegment(questionId, nameof(questionId));
            return $"q:{agentId}:{questionId}";
        }

        /// <summary>
        /// Computes the idempotency key for an <see cref="OutboundMessageSource.Alert"/>.
        /// Format: <c>alert:{agentId}:{alertId}</c>.
        /// </summary>
        public static string ForAlert(string agentId, string alertId)
        {
            ValidateKeySegment(agentId, nameof(agentId));
            ValidateKeySegment(alertId, nameof(alertId));
            return $"alert:{agentId}:{alertId}";
        }

        /// <summary>
        /// Computes the idempotency key for an <see cref="OutboundMessageSource.StatusUpdate"/>.
        /// Format: <c>s:{agentId}:{correlationId}</c> — status updates collapse
        /// per (agent, trace) pair so repeat publishes within a trace fold to
        /// a single outbound row.
        /// </summary>
        public static string ForStatusUpdate(string agentId, string correlationId)
        {
            ValidateKeySegment(agentId, nameof(agentId));
            ValidateKeySegment(correlationId, nameof(correlationId));
            return $"s:{agentId}:{correlationId}";
        }

        /// <summary>
        /// Computes the idempotency key for an <see cref="OutboundMessageSource.CommandAck"/>.
        /// Format: <c>c:{correlationId}</c> — command acknowledgements collapse
        /// per trace and intentionally have no agent component (the trace is
        /// already globally unique to the slash-command invocation).
        /// </summary>
        public static string ForCommandAck(string correlationId)
        {
            ValidateKeySegment(correlationId, nameof(correlationId));
            return $"c:{correlationId}";
        }
    }

    private static void ValidateKeySegment(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{paramName} must not be null, empty, or whitespace.",
                paramName);
        }

        if (value.Contains(':'))
        {
            throw new ArgumentException(
                $"{paramName} must not contain ':' (reserved separator in OutboundMessage.IdempotencyKey).",
                paramName);
        }
    }
}
