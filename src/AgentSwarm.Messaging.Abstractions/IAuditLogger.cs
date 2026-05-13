namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Immutable record of a single auditable event flowing through the messenger
/// gateway. Concrete persistence entities (e.g. <c>AuditLogEntry</c> in
/// Stage 5.3) map from this DTO and may add tenant / platform fields at the
/// persistence boundary.
/// </summary>
/// <remarks>
/// This is the GENERAL audit entry used by command receipts, lifecycle
/// events, and unauthorized-rejection notes — fields that are optional in
/// those contexts (e.g. <see cref="MessageId"/>, <see cref="AgentId"/>) are
/// nullable here. For human responses to agent questions — which the story
/// brief mandates persist message-ID / user-ID / agent-ID / timestamp /
/// correlation-ID for every reply — use <see cref="HumanResponseAuditEntry"/>
/// instead; its required modifiers enforce the contract at compile time.
/// </remarks>
public sealed record AuditEntry
{
    private readonly string _correlationId = null!;

    public required Guid EntryId { get; init; }

    /// <summary>
    /// Identifier of the inbound or outbound message this entry describes;
    /// <c>null</c> for entries that are not message-scoped.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>External messenger user identifier of the actor.</summary>
    public required string UserId { get; init; }

    /// <summary>Identifier of the agent involved, when applicable.</summary>
    public string? AgentId { get; init; }

    /// <summary>Short verb describing the action (e.g. <c>command.received</c>).</summary>
    public required string Action { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    /// <summary>Free-form additional context (JSON, error detail, etc.).</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Strongly-typed audit entry for human responses to agent questions
/// (approvals, rejections, comments, timeouts). The story brief mandates:
/// <i>"Persist every human response with message ID, user ID, agent ID,
/// timestamp, and correlation ID."</i> Every field that requirement names is
/// marked <c>required</c>, so the compiler rejects any construction that
/// omits one — the contract is enforced at the type level, not at runtime.
/// </summary>
public sealed record HumanResponseAuditEntry
{
    private readonly string _correlationId = null!;

    public required Guid EntryId { get; init; }

    /// <summary>
    /// Identifier of the inbound message carrying the human reply
    /// (Telegram <c>message_id</c> or callback-query id). Mandatory.
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>External messenger user identifier of the responder. Mandatory.</summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Identifier of the agent whose question is being answered. Mandatory.
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>Identifier of the question being answered.</summary>
    public required string QuestionId { get; init; }

    /// <summary>
    /// Canonical <see cref="HumanAction.Value"/> the operator selected
    /// (or <c>__timeout__</c> when timed-out).
    /// </summary>
    public required string ActionValue { get; init; }

    /// <summary>Optional follow-up comment text from the operator.</summary>
    public string? Comment { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }
}

/// <summary>
/// Persists audit entries for every human/agent interaction passing through
/// the gateway. Two overloads expose the general-purpose
/// <see cref="AuditEntry"/> path and the type-enforced
/// <see cref="HumanResponseAuditEntry"/> path for human replies.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>
    /// Persist a human response. The strongly-typed parameter enforces
    /// presence of the five mandatory fields from the story brief at
    /// compile time.
    /// </summary>
    Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct);
}
