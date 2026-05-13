namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Canonical payload-type discriminators for an <see cref="OutboxEntry"/>. Aligned with
/// <c>architecture.md</c> §3.2 which lists <c>MessengerMessage</c> and <c>AgentQuestion</c>
/// as the two payload variants the outbox transports.
/// </summary>
public static class OutboxPayloadTypes
{
    /// <summary>Outbound <see cref="AgentSwarm.Messaging.Abstractions.MessengerMessage"/>.</summary>
    public const string MessengerMessage = "MessengerMessage";

    /// <summary>Outbound <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/> card.</summary>
    public const string AgentQuestion = "AgentQuestion";

    /// <summary>All canonical payload-type discriminators.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { MessengerMessage, AgentQuestion };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
