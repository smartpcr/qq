namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Lifecycle states for an <see cref="AgentQuestion"/>. Transitions are managed by the
/// question store (Open → Resolved on first human decision, Open → Expired by the expiry
/// worker). Aligned with architecture.md §3.1 AgentQuestion.Status.
/// </summary>
public static class AgentQuestionStatuses
{
    /// <summary>Initial state — awaiting a human response.</summary>
    public const string Open = "Open";

    /// <summary>A human decision has been recorded — terminal.</summary>
    public const string Resolved = "Resolved";

    /// <summary>The question's <see cref="AgentQuestion.ExpiresAt"/> elapsed without a response — terminal.</summary>
    public const string Expired = "Expired";

    /// <summary>All canonical lifecycle states.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Open, Resolved, Expired };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
