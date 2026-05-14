namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Durable record of a Teams conversation between the bot and a target user (or channel)
/// captured the first time the bot is granted a conversation hook (install or first reply).
/// Re-hydrated by <c>TeamsProactiveNotifier</c> via <c>CloudAdapter.ContinueConversationAsync</c>
/// so the bot can proactively send Adaptive Cards without waiting for a user-initiated turn.
/// Aligned with <c>architecture.md</c> §4.2 dual-key model.
/// </summary>
/// <remarks>
/// <para>
/// Two identity keys are recorded simultaneously:
/// <see cref="AadObjectId"/> is the Teams-native identity captured from
/// <c>Activity.From.AadObjectId</c>, used for fast lookup from inbound activities.
/// <see cref="InternalUserId"/> is the orchestrator's platform-agnostic user identifier
/// resolved by <c>IIdentityResolver</c>, used for proactive routing keyed by
/// <c>AgentQuestion.TargetUserId</c>.
/// </para>
/// <para>
/// Channel-scoped references set <see cref="ChannelId"/> instead of either user identifier;
/// the dual-key user fields are <see langword="null"/> in that case.
/// </para>
/// </remarks>
public sealed record TeamsConversationReference
{
    /// <summary>Entra ID tenant of the user or channel.</summary>
    public required string TenantId { get; init; }

    /// <summary>AAD object ID of the user (Teams-native identity key). Null for channel-scoped references.</summary>
    public string? AadObjectId { get; init; }

    /// <summary>Internal user ID assigned by <c>IIdentityResolver</c> (orchestrator key). Null for channel-scoped references.</summary>
    public string? InternalUserId { get; init; }

    /// <summary>Teams channel ID. Null for user-scoped references.</summary>
    public string? ChannelId { get; init; }

    /// <summary>The fully-serialized <c>ConversationReference</c> JSON used by
    /// <c>CloudAdapter.ContinueConversationAsync</c> to re-hydrate the conversation for
    /// proactive sends.</summary>
    public required string SerializedReference { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC last-update timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Whether the reference may still be used for proactive sends. Set to
    /// <see langword="false"/> when the bot is uninstalled or the channel is deleted.</summary>
    public bool IsActive { get; init; } = true;
}
