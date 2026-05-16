using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Immutable audit row stored by <see cref="IAuditLogger.LogAsync"/>. Mirrors
/// the field set of the shared <see cref="AuditEntry"/> DTO with a surrogate
/// <see cref="Id"/> primary key added so EF can track inserts without
/// fabricating a composite natural key. Per architecture.md Section 3.1
/// (AuditLogEntry) and Section 4.10, connector-specific identifiers
/// (Discord guild/channel/interaction/thread ids) are carried in
/// <see cref="Details"/> as a JSON object so the schema stays platform-neutral.
/// </summary>
public class AuditLogEntry
{
    /// <summary>
    /// Surrogate primary key. Client-assigned at construction via the
    /// property initializer (<see cref="Guid.NewGuid"/>) so freshly created
    /// instances never collide on <see cref="Guid.Empty"/>. EF is configured
    /// with <c>ValueGeneratedNever</c> to respect this client value rather
    /// than overwrite it; SQLite has no built-in Guid generator so server-
    /// side assignment is not an option.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Messenger platform identifier (<c>"Discord"</c>, <c>"Telegram"</c>,
    /// <c>"Slack"</c>, <c>"Teams"</c>). Required.
    /// </summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// Platform-native user identifier of the actor (Discord user snowflake
    /// stringified, Telegram user id, Slack member id, AAD object id for
    /// Teams). Required.
    /// </summary>
    public string ExternalUserId { get; set; } = string.Empty;

    /// <summary>
    /// Platform-native message identifier the entry refers to. For Discord
    /// this is the message snowflake (stringified) of the bot's response.
    /// Inbound interaction ids belong in <see cref="Details"/> under the
    /// <c>InteractionId</c> key, not here. Required.
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Connector-specific structured data, JSON-serialized. For Discord this
    /// typically contains <c>GuildId</c>, <c>ChannelId</c>,
    /// <c>InteractionId</c>, and <c>ThreadId</c>. Required (use <c>"{}"</c>
    /// when no extras apply).
    /// </summary>
    public string Details { get; set; } = "{}";

    /// <summary>When the audited event occurred.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>End-to-end trace identifier propagated from the source event.</summary>
    public string CorrelationId { get; set; } = string.Empty;
}
