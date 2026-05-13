namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Canonical <see cref="AuditEntry.ActorType"/> vocabulary defined by
/// <c>tech-spec.md</c> §4.3. Disambiguates whether <see cref="AuditEntry.ActorId"/> is a
/// human identity (AAD object ID) or a service identity (agent ID).
/// </summary>
public static class AuditActorTypes
{
    /// <summary>The actor is a human user. <see cref="AuditEntry.ActorId"/> is their AAD object ID.</summary>
    public const string User = "User";

    /// <summary>The actor is an agent (service identity). <see cref="AuditEntry.ActorId"/> is the agent ID.</summary>
    public const string Agent = "Agent";

    /// <summary>All canonical actor-type values.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { User, Agent };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
