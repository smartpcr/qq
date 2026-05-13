namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Canonical severity values shared between <see cref="MessengerMessage.Severity"/> and
/// <see cref="AgentQuestion.Severity"/>. Aligned with architecture.md §3.1.
/// </summary>
public static class MessageSeverities
{
    /// <summary>Informational message — no action required.</summary>
    public const string Info = "Info";

    /// <summary>Warning — attention may be required.</summary>
    public const string Warning = "Warning";

    /// <summary>Error — an operation failed.</summary>
    public const string Error = "Error";

    /// <summary>Critical — immediate human intervention required.</summary>
    public const string Critical = "Critical";

    /// <summary>All canonical severity values, in escalating order.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Info, Warning, Error, Critical };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
