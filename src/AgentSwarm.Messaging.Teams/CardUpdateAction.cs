namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Card-state update vocabulary surfaced through <see cref="ITeamsCardManager.UpdateCardAsync"/>.
/// Aligned with <c>architecture.md</c> §4.1.1.
/// </summary>
public enum CardUpdateAction
{
    /// <summary>The card was answered by a human decision.</summary>
    MarkAnswered = 1,

    /// <summary>The card's deadline elapsed without a decision.</summary>
    MarkExpired = 2,

    /// <summary>The card was cancelled by the orchestrator (e.g. task aborted).</summary>
    MarkCancelled = 3,
}
