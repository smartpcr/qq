namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Canonical discriminator values for <see cref="MessengerEvent.EventType"/>. Aligned with
/// architecture.md §3.1 cross-doc alignment note and e2e-scenarios.md §Correlation and Traceability.
/// </summary>
public static class MessengerEventTypes
{
    /// <summary>User invoked <c>agent ask &lt;text&gt;</c> to create a task. Subtype: <see cref="CommandEvent"/>.</summary>
    public const string AgentTaskRequest = "AgentTaskRequest";

    /// <summary>General-purpose command (<c>agent status</c>, bare <c>approve</c>, bare <c>reject</c>). Subtype: <see cref="CommandEvent"/>.</summary>
    public const string Command = "Command";

    /// <summary>User invoked <c>escalate</c>. Subtype: <see cref="CommandEvent"/>.</summary>
    public const string Escalation = "Escalation";

    /// <summary>User invoked <c>pause</c>. Subtype: <see cref="CommandEvent"/>.</summary>
    public const string PauseAgent = "PauseAgent";

    /// <summary>User invoked <c>resume</c>. Subtype: <see cref="CommandEvent"/>.</summary>
    public const string ResumeAgent = "ResumeAgent";

    /// <summary>User tapped an Adaptive Card action button. Subtype: <see cref="DecisionEvent"/>.</summary>
    public const string Decision = "Decision";

    /// <summary>Unrecognized free-text input. Subtype: <see cref="TextEvent"/>.</summary>
    public const string Text = "Text";

    /// <summary>Bot installation or uninstallation event.</summary>
    public const string InstallUpdate = "InstallUpdate";

    /// <summary>User added or removed a reaction to a bot message.</summary>
    public const string Reaction = "Reaction";

    /// <summary>All canonical event-type discriminator values.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        AgentTaskRequest,
        Command,
        Escalation,
        PauseAgent,
        ResumeAgent,
        Decision,
        Text,
        InstallUpdate,
        Reaction,
    };

    /// <summary>Subset of <see cref="All"/> that may be carried by a <see cref="CommandEvent"/>.</summary>
    public static IReadOnlyList<string> CommandEventTypes { get; } = new[]
    {
        AgentTaskRequest,
        Command,
        Escalation,
        PauseAgent,
        ResumeAgent,
    };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="CommandEventTypes"/>.</summary>
    public static bool IsCommandEventType(string? value) => value is not null && CommandEventTypes.Contains(value);
}
