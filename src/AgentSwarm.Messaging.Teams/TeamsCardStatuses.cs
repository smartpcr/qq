namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Canonical lifecycle states for a <see cref="TeamsCardState"/>. Aligned with
/// <c>implementation-plan.md</c> §2.1 step 3 (<c>Pending</c> / <c>Answered</c> /
/// <c>Expired</c>) and <c>architecture.md</c> §3.2 (which adds <c>Deleted</c> for the
/// hard-delete path used by <c>ITeamsCardManager.DeleteCardAsync</c>).
/// </summary>
public static class TeamsCardStatuses
{
    /// <summary>Card has been delivered and is awaiting a human action.</summary>
    public const string Pending = "Pending";

    /// <summary>The card has been answered by a human action — terminal.</summary>
    public const string Answered = "Answered";

    /// <summary>The originating question expired before any answer was recorded — terminal.</summary>
    public const string Expired = "Expired";

    /// <summary>The card was deleted (manually or via <c>ITeamsCardManager.DeleteCardAsync</c>) — terminal.</summary>
    public const string Deleted = "Deleted";

    /// <summary>All canonical card-lifecycle states.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Pending, Answered, Expired, Deleted };

    /// <summary>Returns <c>true</c> when <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
