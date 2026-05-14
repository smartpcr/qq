namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Canonical <see cref="AuditEntry.EventType"/> vocabulary defined by
/// <c>tech-spec.md</c> §4.3 (Canonical Audit Record Schema). The canonical set contains
/// exactly seven values.
/// </summary>
/// <remarks>
/// <para>
/// The audit <c>EventType</c> is intentionally a different concept from the
/// <see cref="AgentSwarm.Messaging.Abstractions.MessengerEvent.EventType"/> discriminator on
/// the domain model: the audit value categorizes audit-log entries while the messenger
/// event discriminator identifies the domain event subtype.
/// </para>
/// <para>
/// Message actions (Teams message-extension submissions arriving via
/// <c>composeExtension/submitAction</c>) log as <see cref="MessageActionReceived"/> — a
/// dedicated audit event type distinct from <see cref="CommandReceived"/> — because
/// distinguishing them in the audit trail supports compliance filtering and forensic
/// analysis.
/// </para>
/// </remarks>
public static class AuditEventTypes
{
    /// <summary>A user-issued command (<c>agent ask</c>, <c>approve</c>, <c>reject</c>, etc.) was received.</summary>
    public const string CommandReceived = "CommandReceived";

    /// <summary>An outbound message was sent (or attempted) to a messenger.</summary>
    public const string MessageSent = "MessageSent";

    /// <summary>A human tapped an Adaptive Card action button.</summary>
    public const string CardActionReceived = "CardActionReceived";

    /// <summary>An inbound activity was rejected because of tenant/identity/RBAC policy.</summary>
    public const string SecurityRejection = "SecurityRejection";

    /// <summary>A proactive (agent-initiated) notification was delivered (or attempted).</summary>
    public const string ProactiveNotification = "ProactiveNotification";

    /// <summary>A Teams message-extension submission (<c>composeExtension/submitAction</c>) was received.</summary>
    public const string MessageActionReceived = "MessageActionReceived";

    /// <summary>An unexpected error occurred while handling an inbound or outbound event.</summary>
    public const string Error = "Error";

    /// <summary>All canonical audit event-type values (exactly seven entries).</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        CommandReceived,
        MessageSent,
        CardActionReceived,
        SecurityRejection,
        ProactiveNotification,
        MessageActionReceived,
        Error,
    };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
