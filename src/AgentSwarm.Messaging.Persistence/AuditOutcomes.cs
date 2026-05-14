namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Canonical <see cref="AuditEntry.Outcome"/> vocabulary defined by
/// <c>tech-spec.md</c> §4.3.
/// </summary>
public static class AuditOutcomes
{
    /// <summary>The action completed successfully.</summary>
    public const string Success = "Success";

    /// <summary>The action was rejected by policy (tenant / identity / RBAC).</summary>
    public const string Rejected = "Rejected";

    /// <summary>The action attempted but failed (e.g., transport or transient failure).</summary>
    public const string Failed = "Failed";

    /// <summary>The action exhausted retries and was moved to the dead-letter queue.</summary>
    public const string DeadLettered = "DeadLettered";

    /// <summary>All canonical outcome values.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Success, Rejected, Failed, DeadLettered };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
