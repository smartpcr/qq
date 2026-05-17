namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Canonical destination-type discriminators for an <see cref="OutboxEntry"/>. Aligned with
/// <c>implementation-plan.md</c> §6.1 which specifies <c>DestinationType</c> as
/// <c>personal</c>/<c>channel</c> identifying the target scope for outbound send
/// operations.
/// </summary>
public static class OutboxDestinationTypes
{
    /// <summary>Personal (1:1) chat with a user.</summary>
    public const string Personal = "Personal";

    /// <summary>Team channel.</summary>
    public const string Channel = "Channel";

    /// <summary>All canonical destination-type discriminators.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Personal, Channel };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
