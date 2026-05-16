namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Audit entry capturing a human operator's resolution of an
/// <see cref="AgentQuestion"/>. Distinguished from the generic
/// <see cref="AuditEntry"/> because human decisions carry additional
/// structured fields (which question, which action chosen, optional rationale)
/// that downstream readers (compliance, swarm replay, post-mortems) query
/// directly rather than via the JSON <c>Details</c> bag. See architecture.md
/// Section 4.10.
/// </summary>
/// <param name="Platform">Messenger platform identifier (<c>"Discord"</c>, etc.). Required.</param>
/// <param name="ExternalUserId">Platform-native user id of the responding operator. Required.</param>
/// <param name="MessageId">
/// Platform-native <em>message</em> identifier of the bot's message that the
/// human responded to (Discord message snowflake stringified, Telegram message
/// id, Slack ts, Teams activity id). Required. The Discord interaction
/// snowflake belongs in <see cref="Details"/> under the <c>InteractionId</c>
/// key — see architecture.md Section 3.1 (AuditLogEntry table) and Section 4.10.
/// </param>
/// <param name="QuestionId">Identifier of the <see cref="AgentQuestion"/> being answered.</param>
/// <param name="SelectedActionId">
/// <see cref="HumanAction.ActionId"/> the operator chose.
/// </param>
/// <param name="ActionValue">
/// Resolved <see cref="HumanAction.Value"/> for the selected action.
/// </param>
/// <param name="Comment">
/// Free-form rationale the operator supplied. Required (non-null) when the chosen
/// <see cref="HumanAction.RequiresComment"/> was <see langword="true"/>; otherwise
/// <see langword="null"/>. Mirrors <see cref="HumanDecisionEvent.Comment"/>.
/// </param>
/// <param name="Details">
/// Connector-specific structured data, JSON-serialized (Discord:
/// <c>GuildId</c>, <c>ChannelId</c>, <c>InteractionId</c>, <c>ThreadId</c>).
/// Use <c>"{}"</c> when no extras apply.
/// </param>
/// <param name="Timestamp">When the decision was received.</param>
/// <param name="CorrelationId">End-to-end trace identifier propagated from the originating question.</param>
public sealed record HumanResponseAuditEntry(
    string Platform,
    string ExternalUserId,
    string MessageId,
    string QuestionId,
    string SelectedActionId,
    string ActionValue,
    string? Comment,
    string Details,
    DateTimeOffset Timestamp,
    string CorrelationId);
