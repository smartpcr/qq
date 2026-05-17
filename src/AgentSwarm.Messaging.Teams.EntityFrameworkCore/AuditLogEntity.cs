namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF entity that maps to the append-only <c>AuditLog</c> table. Mirrors the canonical
/// <see cref="AgentSwarm.Messaging.Persistence.AuditEntry"/> field-for-field and adds
/// the implementation-specific surrogate <see cref="Id"/> primary key per
/// <c>implementation-plan.md</c> §5.2 step 1.
/// </summary>
/// <remarks>
/// <para>
/// The class is intentionally a plain POCO — all column shape (max length, nullability,
/// indexes, triggers) is configured in <see cref="AuditLogDbContext.OnModelCreating"/>
/// and in the EF migration. Keeping the entity dumb here means the canonical schema
/// stays a single-source-of-truth in the migration and there is no risk of EF
/// inferring something out of sync with the actual table when
/// <c>EnsureCreated</c> runs in test fixtures.
/// </para>
/// <para>
/// The entity is mutable on its surface (auto-properties with setters) because EF
/// materialises it via property setters when reading. <see cref="SqlAuditLogger"/>
/// never mutates an existing row — it only inserts new ones — so the entity's
/// mutability does not violate the canonical immutability invariant on the
/// <see cref="AgentSwarm.Messaging.Persistence.AuditEntry"/> contract. The database
/// triggers installed by the migration enforce immutability at the storage layer.
/// </para>
/// </remarks>
public sealed class AuditLogEntity
{
    /// <summary>Surrogate primary key (auto-incremented). Non-clustered so the clustered index can target <see cref="Timestamp"/>.</summary>
    public long Id { get; set; }

    /// <summary>UTC time the event occurred.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>End-to-end trace ID for distributed tracing.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Audit category — one of <see cref="AgentSwarm.Messaging.Persistence.AuditEventTypes.All"/>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Actor identity — AAD object ID for users, agent ID for agent-originated events.</summary>
    public string ActorId { get; set; } = string.Empty;

    /// <summary>One of <see cref="AgentSwarm.Messaging.Persistence.AuditActorTypes.All"/>: <c>User</c> or <c>Agent</c>.</summary>
    public string ActorType { get; set; } = string.Empty;

    /// <summary>Entra ID tenant of the actor.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The agent whose task or question triggered this event; null when not associated with a specific agent.</summary>
    public string? AgentId { get; set; }

    /// <summary>Agent task or work-item ID; null for events outside a task context.</summary>
    public string? TaskId { get; set; }

    /// <summary>Teams (or other messenger) conversation ID; null for events outside a conversation.</summary>
    public string? ConversationId { get; set; }

    /// <summary>The specific action taken.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>JSON-serialized event payload (sanitized).</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>One of <see cref="AgentSwarm.Messaging.Persistence.AuditOutcomes.All"/>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>SHA-256 digest (lower-case hex) over the canonical fields for tamper detection.</summary>
    public string Checksum { get; set; } = string.Empty;
}
