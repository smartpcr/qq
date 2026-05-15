namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persistence contract for <see cref="TeamsConversationReference"/> records, defined in the
/// <c>AgentSwarm.Messaging.Teams</c> assembly per <c>implementation-plan.md</c> §2.1 (the
/// single source of truth for the interface). The concrete in-memory implementation is
/// registered in Stage 2.1 DI; the SQL-backed implementation lands in Stage 4.1.
/// </summary>
/// <remarks>
/// <para>
/// Aligned with <c>architecture.md</c> §4.2 and <c>implementation-plan.md</c> §2.1. The
/// dual-key model (separate <see cref="TeamsConversationReference.AadObjectId"/> and
/// <see cref="TeamsConversationReference.InternalUserId"/> fields) supports two lookup
/// paths: <see cref="GetByAadObjectIdAsync"/> by Teams-native identity (captured from the
/// inbound activity) and <see cref="GetByInternalUserIdAsync"/> by orchestrator-native
/// identity (used for proactive delivery via <c>AgentQuestion.TargetUserId</c>).
/// </para>
/// <para>
/// <see cref="IsActiveByInternalUserIdAsync"/> and <see cref="IsActiveByChannelAsync"/> are
/// used by <c>InstallationStateGate</c> (Stage 5.1) to verify the target conversation is
/// still active before a proactive send — they avoid the "missing vs. inactive" ambiguity
/// the <see cref="GetByInternalUserIdAsync"/>-only path would otherwise introduce (the
/// getter returns only active rows, making it impossible to distinguish the two states).
/// </para>
/// </remarks>
public interface IConversationReferenceStore
{
    /// <summary>
    /// Insert a new reference or update an existing one keyed by the natural key
    /// (<c>(TenantId, AadObjectId)</c> for personal references; <c>(TenantId, ChannelId)</c>
    /// for channel references).
    /// </summary>
    /// <param name="reference">The reference to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct);

    /// <summary>
    /// General-purpose single-record retrieval for a user-scoped reference keyed by
    /// <c>(TenantId, AadObjectId)</c>. Returns the reference regardless of
    /// <see cref="TeamsConversationReference.IsActive"/> — used for administrative inspection,
    /// audit replay, and post-uninstall lookups. For proactive-send pre-checks use
    /// <see cref="GetByAadObjectIdAsync"/> (active-only) instead.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="aadObjectId">User AAD object ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reference (active or inactive), or <c>null</c> if no row exists.</returns>
    Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>Look up a personal-scope reference by Teams-native AAD object ID.</summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="aadObjectId">User AAD object ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reference, or <c>null</c> if no row exists.</returns>
    Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>Look up a personal-scope reference by orchestrator-native internal user ID (used by proactive routing).</summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="internalUserId">Internal user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reference, or <c>null</c> if no row exists.</returns>
    Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct);

    /// <summary>Look up a channel-scope reference by Teams channel ID.</summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="channelId">Teams channel ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reference, or <c>null</c> if no row exists.</returns>
    Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>
    /// Return every active channel-scope reference belonging to the supplied Teams team.
    /// Used by <c>TeamsSwarmActivityHandler</c> to enumerate the channels that must be
    /// marked inactive when the bot is uninstalled from a team (per
    /// <c>architecture.md</c> §4.2 — "for each channel in the team").
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="teamId">Teams team ID (the parent team of the channels).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Possibly empty list of active channel references for the team.</returns>
    Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct);

    /// <summary>Return every active reference (<c>IsActive = true</c>) for a tenant.</summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Possibly empty list of active references.</returns>
    Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Check whether the personal-scope reference for <paramref name="aadObjectId"/> exists
    /// and is currently active.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="aadObjectId">User AAD object ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the reference exists and <c>IsActive = true</c>; otherwise <c>false</c>.</returns>
    Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>
    /// Check whether the personal-scope reference for <paramref name="internalUserId"/>
    /// exists and is currently active. Mirrors <see cref="IsActiveAsync"/> but uses the
    /// orchestrator-native identity key — used by <c>InstallationStateGate</c> (Stage 5.1)
    /// to verify user-scoped targets before proactive sends.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="internalUserId">Internal user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the reference exists and <c>IsActive = true</c>; otherwise <c>false</c>.</returns>
    Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct);

    /// <summary>
    /// Check whether the channel-scope reference for <paramref name="channelId"/> exists
    /// and is currently active. Used by <c>InstallationStateGate</c> to verify channel
    /// targets before proactive sends.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="channelId">Teams channel ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the reference exists and <c>IsActive = true</c>; otherwise <c>false</c>.</returns>
    Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>
    /// Mark the personal-scope reference inactive (<c>IsActive = false</c>) without
    /// deleting the row. Retained for audit.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="aadObjectId">User AAD object ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>
    /// Mark the channel-scope reference inactive (<c>IsActive = false</c>) without
    /// deleting the row. Called when the bot is uninstalled from a team — invoked once
    /// per channel the bot had references for.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="channelId">Teams channel ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>
    /// Administrative cleanup: hard-delete the personal-scope reference. Should NOT be
    /// invoked during the normal uninstall flow — use <see cref="MarkInactiveAsync"/>
    /// instead so audit history is preserved.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="aadObjectId">User AAD object ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>
    /// Administrative cleanup: hard-delete the channel-scope reference. Should NOT be
    /// invoked during the normal uninstall flow — use <see cref="MarkInactiveByChannelAsync"/>
    /// instead.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="channelId">Teams channel ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct);
}
