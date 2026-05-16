namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Audit log entry for any operator-visible event flowing through a messenger
/// connector (inbound command receipt, outbound message dispatch, authorization
/// decision). Stored by <see cref="IAuditLogger.LogAsync"/>. See architecture.md
/// Section 4.10 — connector-specific identifiers (Discord guild/channel/interaction
/// ids; Telegram chat id; Slack team/channel ids; Teams tenant/conversation ids)
/// are carried in <see cref="Details"/> as a JSON object so the shared schema
/// stays platform-neutral while preserving full per-platform provenance.
/// </summary>
/// <param name="Platform">
/// Messenger platform identifier (<c>"Discord"</c>, <c>"Telegram"</c>, <c>"Slack"</c>,
/// <c>"Teams"</c>). Required.
/// </param>
/// <param name="ExternalUserId">
/// Platform-native user identifier of the actor that triggered the event
/// (Discord user snowflake stringified, Telegram user id, Slack member id, AAD
/// object id for Teams). Required.
/// </param>
/// <param name="MessageId">
/// Platform-native <em>message</em> identifier the entry refers to (Discord
/// message snowflake stringified, Telegram message id, Slack ts, Teams activity
/// id). Required. Inbound interaction identifiers (Discord interaction
/// snowflake) belong in <see cref="Details"/> under the <c>InteractionId</c>
/// key, not here — see architecture.md Section 3.1 (AuditLogEntry table) and
/// Section 4.10. The story-level audit requirement is "Discord message IDs are
/// stored with every command and response", which this field satisfies.
/// </param>
/// <param name="Details">
/// Connector-specific structured data, JSON-serialized. For Discord this
/// typically contains <c>GuildId</c>, <c>ChannelId</c>, <c>InteractionId</c>,
/// and <c>ThreadId</c>. Required (use <c>"{}"</c> when no extras apply).
/// </param>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="CorrelationId">End-to-end trace identifier propagated from the source event.</param>
public sealed record AuditEntry(
    string Platform,
    string ExternalUserId,
    string MessageId,
    string Details,
    DateTimeOffset Timestamp,
    string CorrelationId);
