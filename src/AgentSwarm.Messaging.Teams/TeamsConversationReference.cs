namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persisted Bot Framework <c>ConversationReference</c> for proactive messaging. Aligned with
/// <c>architecture.md</c> §3.2 <see cref="TeamsConversationReference"/> field table and
/// <c>implementation-plan.md</c> §2.1 dual identity-key model.
/// </summary>
/// <remarks>
/// <para>
/// The record carries TWO identity dimensions on every personal-scope reference:
/// <see cref="AadObjectId"/> (the Teams-native identity captured from
/// <c>Activity.From.AadObjectId</c>, used as the persistence key) and
/// <see cref="InternalUserId"/> (the orchestrator's platform-agnostic identity, populated by
/// <c>IIdentityResolver</c> on first authorized interaction and used as the routing key for
/// <c>AgentQuestion.TargetUserId</c>). Channel-scope references set <see cref="ChannelId"/>
/// instead and leave both identity fields null.
/// </para>
/// <para>
/// Stored in the <c>AgentSwarm.Messaging.Teams</c> assembly per the canonical assembly table
/// in <c>architecture.md</c> §7.
/// </para>
/// </remarks>
public sealed record TeamsConversationReference
{
    /// <summary>Primary key — a GUID assigned by the store on first persist.</summary>
    public required string Id { get; init; }

    /// <summary>Entra ID tenant of the user or channel.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Entra AAD object ID of the user. Null for channel-scoped references (where
    /// <see cref="ChannelId"/> is set instead). This is the Teams-native identity key
    /// captured from <c>Activity.From.AadObjectId</c> at install time and refreshed on
    /// message receipt.
    /// </summary>
    public string? AadObjectId { get; init; }

    /// <summary>
    /// Internal user ID mapped by <c>IIdentityResolver</c>. Populated when identity
    /// resolution first succeeds for this AAD object ID. Null until the user's first
    /// authorized interaction. The orchestrator uses this value when setting
    /// <c>AgentQuestion.TargetUserId</c> for proactive delivery.
    /// </summary>
    public string? InternalUserId { get; init; }

    /// <summary>Teams channel ID. Null for personal chats.</summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Teams team ID (the parent team of <see cref="ChannelId"/>). Populated for
    /// channel-scope references so the store can enumerate all channels in a team during
    /// team-scope uninstalls (see <c>architecture.md</c> §4.2). Null for personal-scope
    /// references.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>Bot Connector service URL (rotates — refreshed on every message receipt).</summary>
    public required string ServiceUrl { get; init; }

    /// <summary>Bot Framework conversation ID.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The bot's AAD app ID.</summary>
    public required string BotId { get; init; }

    /// <summary>Serialized <c>ConversationReference</c> JSON for rehydration in background workers.</summary>
    public required string ReferenceJson { get; init; }

    /// <summary>True while the reference is usable for proactive sends. Flipped to false on uninstall.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>UTC creation timestamp (set on first persist).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC last-update timestamp (refreshed every time the reference is re-saved).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
