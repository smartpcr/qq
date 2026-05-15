namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Canonical lifecycle states for a <see cref="TeamsCardState"/>. Aligned with
/// <c>implementation-plan.md</c> §2.1 step 3 and §3.3 step 1 (<c>Pending</c> /
/// <c>Answered</c> / <c>Expired</c>). The implementation plan §209 explicitly enumerates
/// the card-state vocabulary as <c>Pending/Answered/Expired</c>; the
/// <c>ITeamsCardManager.DeleteCardAsync</c> path also lands the row at
/// <see cref="Expired"/> (per §213 step 5 and the §3.3 acceptance scenario in §222),
/// so a separate <c>Deleted</c> status is intentionally NOT defined.
/// </summary>
public static class TeamsCardStatuses
{
    /// <summary>Card has been delivered and is awaiting a human action.</summary>
    public const string Pending = "Pending";

    /// <summary>The card has been answered by a human action — terminal.</summary>
    public const string Answered = "Answered";

    /// <summary>
    /// Terminal state for cards whose originating question expired before any answer was
    /// recorded AND for cards that were removed via
    /// <c>ITeamsCardManager.DeleteCardAsync</c>. The implementation-plan §3.3 spec
    /// uses a single <c>Expired</c> terminal state for both lifecycles to keep the card
    /// vocabulary aligned with the question vocabulary
    /// (<c>AgentQuestionStatuses.Expired</c>).
    /// </summary>
    public const string Expired = "Expired";

    /// <summary>All canonical card-lifecycle states (exactly three entries).</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Pending, Answered, Expired };

    /// <summary>Returns <c>true</c> when <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
