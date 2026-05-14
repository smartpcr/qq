namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persisted Bot Framework <c>ConversationReference</c> for proactive Teams messaging.
/// Aligned with <c>architecture.md</c> §3.2 (Teams-Specific Entities). Carries both the
/// Teams-native identity key (<see cref="AadObjectId"/>) and the orchestrator-side identity
/// key (<see cref="InternalUserId"/>) so that proactive routing keyed by either path
/// resolves to a single canonical record.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="AadObjectId"/> (for personal-scope references) or
/// <see cref="ChannelId"/> (for channel-scoped references) is populated. Channel-scoped
/// references carry no AAD object identity; personal-scope references may carry no channel
/// ID. The dual-key design (separate <see cref="AadObjectId"/> and
/// <see cref="InternalUserId"/> fields) is mandated by the Stage 2.1 implementation plan and
/// is enforced by <see cref="IConversationReferenceStore"/>'s lookup surface.
/// </para>
/// <para>
/// <see cref="ReferenceJson"/> carries the serialized Bot Framework
/// <c>ConversationReference</c> for in-process rehydration when proactive sends run on a
/// worker thread without an active <c>ITurnContext</c>.
/// </para>
/// </remarks>
public sealed record TeamsConversationReference
{
    /// <summary>Primary key (GUID) — surrogate identifier independent of identity keys.</summary>
    public required string Id { get; init; }

    /// <summary>Entra ID tenant ID. Required for all lookups.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Entra AAD object ID of the user. Null for channel-scoped references (where
    /// <see cref="ChannelId"/> is set instead). Captured from
    /// <c>Activity.From.AadObjectId</c> at install time and refreshed on subsequent
    /// authorized message receipts.
    /// </summary>
    public string? AadObjectId { get; init; }

    /// <summary>
    /// Internal user ID mapped by <c>IIdentityResolver</c>. Populated when identity
    /// resolution first succeeds for the <see cref="AadObjectId"/>; null until then. The
    /// orchestrator uses this value when setting <c>AgentQuestion.TargetUserId</c> for
    /// proactive delivery.
    /// </summary>
    public string? InternalUserId { get; init; }

    /// <summary>Teams channel ID (null for personal chats).</summary>
    public string? ChannelId { get; init; }

    /// <summary>Bot Connector endpoint (e.g., <c>https://smba.trafficmanager.net/...</c>).</summary>
    public required string ServiceUrl { get; init; }

    /// <summary>Bot Framework conversation ID.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Bot's AAD app ID (the <c>MicrosoftAppId</c>).</summary>
    public required string BotId { get; init; }

    /// <summary>
    /// Serialized Bot Framework <c>ConversationReference</c> for rehydration on background
    /// threads.
    /// </summary>
    public required string ReferenceJson { get; init; }

    /// <summary>False after the bot is uninstalled. Retained for audit (not deleted).</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>First installation/save UTC time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last refresh UTC time.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
