namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Canonical lifecycle states for an <see cref="OutboxEntry"/>. Aligned with
/// <c>architecture.md</c> §3.2 and <c>implementation-plan.md</c> §6.1 outbox status
/// vocabulary.
/// </summary>
public static class OutboxEntryStatuses
{
    /// <summary>Initial state — enqueued but not yet picked up for delivery.</summary>
    public const string Pending = "Pending";

    /// <summary>Picked up by <c>OutboxRetryEngine</c> for delivery (in-flight).</summary>
    public const string Processing = "Processing";

    /// <summary>Successfully delivered to the messenger.</summary>
    public const string Sent = "Sent";

    /// <summary>Last delivery attempt failed; awaiting retry per <c>NextRetryAt</c>.</summary>
    public const string Failed = "Failed";

    /// <summary>Retries exhausted; entry parked for human review.</summary>
    public const string DeadLettered = "DeadLettered";

    /// <summary>All canonical lifecycle states.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Pending,
        Processing,
        Sent,
        Failed,
        DeadLettered,
    };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
