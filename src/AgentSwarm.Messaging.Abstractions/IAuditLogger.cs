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

    /// <summary>
    /// Swarm tenant this audit entry is scoped to (sourced from the
    /// resolved <c>OperatorBinding.TenantId</c> when an
    /// <see cref="AuthorizedOperator"/> is available; <c>null</c> for
    /// entries emitted before authorization resolved a binding, e.g.
    /// the unauthorized-rejection lifecycle row written by the
    /// pipeline before <c>IUserAuthorizationService.AuthorizeAsync</c>
    /// returns). Mapped onto <c>AuditLogEntry.TenantId</c> by the
    /// Stage 5.3 persistence layer (per architecture.md §3.1).
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Free-form additional context (JSON). When non-<see langword="null"/>
    /// the string MUST parse as a valid JSON document — the persistence
    /// boundary validates this via
    /// <c>System.Text.Json.JsonDocument.Parse</c> and throws
    /// <see cref="ArgumentException"/> on invalid shape. The Stage 5.3
    /// column is typed <c>Details (JSON)</c>; callers SHOULD use
    /// <c>System.Text.Json.JsonSerializer.Serialize(...)</c> on a
    /// strongly-typed payload rather than hand-formatting a string.
    /// Pass <see langword="null"/> when no details apply (empty / blank
    /// strings are rejected — JSON requires at least one token).
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Domain-level discriminator describing the kind of event this entry
    /// represents — e.g. <c>"command"</c> for a slash command received by
    /// the router, <c>"handoff"</c> for an oversight transfer,
    /// <c>"lifecycle"</c> for unauthorized rejections / system events,
    /// <c>"decision"</c> for a human decision (the
    /// <see cref="HumanResponseAuditEntry"/> path always uses
    /// <c>"decision"</c>). Defaults to <c>"general"</c> when the producer
    /// does not classify the event. See <see cref="AuditEventFamilies"/>
    /// for the canonical literals.
    /// </summary>
    /// <remarks>
    /// Distinct from the persistence-layer <c>EntryKind</c> discriminator
    /// (<c>general</c> / <c>human-response</c>), which records which
    /// abstraction record produced the row. <c>EventFamily</c> describes
    /// the domain meaning so the Stage 5.3 acceptance assertion
    /// "<c>Action=ask</c>" (the verb) can sit alongside "this is a
    /// command-family event" without overloading <see cref="Action"/>.
    /// </remarks>
    public string EventFamily { get; init; } = AuditEventFamilies.General;
}

/// <summary>
/// Canonical string literals for <see cref="AuditEntry.EventFamily"/> and
/// the equivalent column on <c>AuditLogEntry</c> in the persistence layer.
/// </summary>
public static class AuditEventFamilies
{
    /// <summary>Slash-command receipt (audited by <c>CommandRouter</c>).</summary>
    public const string Command = "command";

    /// <summary>Human decision (approve / reject / button press / timeout).</summary>
    public const string Decision = "decision";

    /// <summary>Oversight handoff transfer.</summary>
    public const string Handoff = "handoff";

    /// <summary>
    /// System lifecycle event not attributable to a specific command verb
    /// (e.g. unauthorized rejection, dedup short-circuit, recovery sweep).
    /// </summary>
    public const string Lifecycle = "lifecycle";

    /// <summary>Default — caller did not classify the event.</summary>
    public const string General = "general";
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

    /// <summary>
    /// Swarm tenant this human response is scoped to (sourced from the
    /// resolved <c>OperatorBinding.TenantId</c> for the responding
    /// operator). Optional at the abstraction layer because the
    /// callback / text-reply paths may not always have an authorized
    /// binding hydrated at audit-write time; the Stage 5.3 persistence
    /// layer maps it onto <c>AuditLogEntry.TenantId</c> verbatim.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Free-form additional context (JSON, workspace info, callback id,
    /// etc.). Persisted onto <c>AuditLogEntry.Details</c> so decision
    /// audit rows carry the full tenant/workspace context required by
    /// Stage 5.3 even though the strongly-typed
    /// <see cref="QuestionId"/> / <see cref="ActionValue"/> /
    /// <see cref="Comment"/> columns already cover the response shape.
    /// When non-<see langword="null"/> the string MUST parse as a valid
    /// JSON document — the persistence boundary validates this via
    /// <c>System.Text.Json.JsonDocument.Parse</c> and throws
    /// <see cref="ArgumentException"/> on invalid shape, per the Stage 5.3
    /// <c>Details (JSON)</c> column contract. Pass <see langword="null"/>
    /// when no details apply.
    /// </summary>
    public string? Details { get; init; }
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
