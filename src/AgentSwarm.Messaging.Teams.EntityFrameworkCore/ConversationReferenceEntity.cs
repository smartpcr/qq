namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core entity backing the <c>ConversationReferences</c> table — the durable form of
/// <see cref="TeamsConversationReference"/> persisted by
/// <see cref="SqlConversationReferenceStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Field set is the union of <c>architecture.md</c> §3.2
/// <see cref="TeamsConversationReference"/> and the Stage 4.1 implementation-plan
/// requirements: every field on the domain record plus three audit-retention fields
/// (<see cref="DeactivatedAt"/>, <see cref="DeactivationReason"/>) added at the persistence
/// layer so uninstall events can be replayed without losing the cause.
/// </para>
/// <para>
/// The table supports two scopes:
/// <list type="bullet">
/// <item><description><b>User-scoped</b> — <see cref="AadObjectId"/> set, <see cref="ChannelId"/> null;
/// upserted on <c>(AadObjectId, TenantId)</c>.</description></item>
/// <item><description><b>Channel-scoped</b> — <see cref="ChannelId"/> set, <see cref="AadObjectId"/> null;
/// upserted on <c>(ChannelId, TenantId)</c>.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ConversationReferenceEntity
{
    /// <summary>Primary-key GUID assigned by <see cref="SqlConversationReferenceStore"/> on first persist.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Entra ID tenant of the user or channel.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Entra AAD object ID of the user. Null for channel-scoped references. Captured from
    /// <c>Activity.From.AadObjectId</c> at install time and refreshed on message receipt.
    /// </summary>
    public string? AadObjectId { get; set; }

    /// <summary>
    /// Internal user ID mapped by <c>IIdentityResolver</c>. Populated when identity
    /// resolution first succeeds for this AAD object ID. Null until the user's first
    /// authorized interaction.
    /// </summary>
    public string? InternalUserId { get; set; }

    /// <summary>Teams channel ID. Null for personal-chat references.</summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Teams team ID (the parent team of <see cref="ChannelId"/>). Populated for
    /// channel-scope references so the store can enumerate every channel reference for a
    /// team during team-scope uninstalls.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>Bot Connector service URL (rotates — refreshed on every message receipt).</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>Bot Framework conversation ID.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The bot's AAD app ID.</summary>
    public string BotId { get; set; } = string.Empty;

    /// <summary>
    /// Serialized Bot Framework <c>ConversationReference</c> JSON for rehydration in
    /// background workers. Aligned with the implementation-plan name <c>ConversationJson</c>
    /// and exposed to callers as <see cref="TeamsConversationReference.ReferenceJson"/>.
    /// </summary>
    public string ConversationJson { get; set; } = string.Empty;

    /// <summary>True while the reference is usable for proactive sends. Flipped to false on uninstall.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Timestamp when the reference was marked inactive. Null while <see cref="IsActive"/> is true.</summary>
    public DateTimeOffset? DeactivatedAt { get; set; }

    /// <summary>
    /// Reason the reference was marked inactive. <c>Uninstalled</c> when set by
    /// <see cref="SqlConversationReferenceStore.MarkInactiveAsync"/> or
    /// <see cref="SqlConversationReferenceStore.MarkInactiveByChannelAsync"/>;
    /// <c>StaleReference</c> reserved for the proactive-notifier reactive 403/404 detector
    /// (Stage 4.2). Null while <see cref="IsActive"/> is true.
    /// </summary>
    public string? DeactivationReason { get; set; }

    /// <summary>UTC creation timestamp (set on first persist).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC last-update timestamp (refreshed every time the reference is re-saved).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
