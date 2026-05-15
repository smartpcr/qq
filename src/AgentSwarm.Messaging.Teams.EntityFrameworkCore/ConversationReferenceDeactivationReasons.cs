namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// Closed vocabulary for <see cref="ConversationReferenceEntity.DeactivationReason"/>.
/// </summary>
public static class ConversationReferenceDeactivationReasons
{
    /// <summary>The bot was uninstalled (personal-chat or team-scope) — set by
    /// <see cref="SqlConversationReferenceStore.MarkInactiveAsync"/> and
    /// <see cref="SqlConversationReferenceStore.MarkInactiveByChannelAsync"/>.</summary>
    public const string Uninstalled = "Uninstalled";

    /// <summary>A 403/404 from Bot Framework signaled the reference is no longer valid (e.g.,
    /// user removed from tenant). Used by the Stage 4.2 proactive-notifier reactive
    /// invalidation path.</summary>
    public const string StaleReference = "StaleReference";
}
