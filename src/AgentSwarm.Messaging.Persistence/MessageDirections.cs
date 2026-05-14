namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Canonical values for <see cref="PersistedMessage.Direction"/> — distinguishes inbound
/// activities received from a messenger from outbound messages sent to a messenger.
/// </summary>
public static class MessageDirections
{
    /// <summary>Inbound — received from a messenger connector (user → bot).</summary>
    public const string Inbound = "Inbound";

    /// <summary>Outbound — sent to a messenger connector (bot → user/channel).</summary>
    public const string Outbound = "Outbound";

    /// <summary>All canonical direction values.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Inbound, Outbound };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
