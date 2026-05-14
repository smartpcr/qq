namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persists Teams <c>ConversationReference</c> records keyed by tenant + AAD object ID (for
/// personal-scope references) or tenant + channel ID (for channel-scoped references). The
/// dual-key model — separate <see cref="TeamsConversationReference.AadObjectId"/> and
/// <see cref="TeamsConversationReference.InternalUserId"/> lookups — is mandated by Stage 2.1
/// and aligned with <c>architecture.md</c> §4.2.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.1 registers <see cref="InMemoryConversationReferenceStore"/> as the default DI
/// implementation. Stage 4.1 swaps in <c>SqlConversationReferenceStore</c> for durable
/// storage. The interface lives in <c>AgentSwarm.Messaging.Teams</c> (not Abstractions)
/// because <see cref="TeamsConversationReference"/> carries Teams-specific fields.
/// </para>
/// <para>
/// <c>MarkInactiveAsync</c> /
/// <c>MarkInactiveByChannelAsync</c> retain the row for audit; <c>DeleteAsync</c> /
/// <c>DeleteByChannelAsync</c> are reserved for administrative cleanup only (post-retention).
/// </para>
/// </remarks>
public interface IConversationReferenceStore
{
    /// <summary>
    /// Persist a new reference or update an existing one. Upsert keyed by the natural
    /// identity (tenant + AAD object ID for personal-scope, tenant + channel ID for
    /// channel-scoped).
    /// </summary>
    /// <param name="reference">The reference to save or update.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>A task that completes when the upsert has been persisted.</returns>
    Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct);

    /// <summary>
    /// Generic lookup by surrogate primary key.
    /// </summary>
    /// <param name="referenceId">The <see cref="TeamsConversationReference.Id"/> to look up.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>The reference, or <c>null</c> when no row matches.</returns>
    Task<TeamsConversationReference?> GetAsync(string referenceId, CancellationToken ct);

    /// <summary>
    /// Return every active reference (personal- and channel-scoped) for the supplied tenant.
    /// Inactive references are excluded.
    /// </summary>
    /// <param name="tenantId">The Entra ID tenant to filter on.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Snapshot list of active references for the tenant.</returns>
    Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Lookup by Teams-native AAD object ID. Returns the active reference (if any) — does
    /// not return inactive rows.
    /// </summary>
    Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>
    /// Lookup by orchestrator-internal user ID (used for proactive routing when the
    /// orchestrator knows only the internal identity). Returns the active reference (if
    /// any).
    /// </summary>
    Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct);

    /// <summary>
    /// Lookup by Teams channel ID for channel-scoped references. Returns the active
    /// reference (if any).
    /// </summary>
    Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>
    /// Mark a personal-scope reference inactive without deleting it (audit retention).
    /// Called on user-scope uninstall.
    /// </summary>
    Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>
    /// Mark a channel-scope reference inactive without deleting it (audit retention).
    /// Called per-channel on team-scope uninstall.
    /// </summary>
    Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>
    /// Personal-scope user-keyed active check. Used by <c>InstallationStateGate</c> in
    /// Stage 5.1 to verify a user-scoped target before proactive sends.
    /// </summary>
    Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>
    /// User-scoped active check keyed by orchestrator-internal user ID. Directly checks
    /// <see cref="TeamsConversationReference.IsActive"/> on the reference keyed by
    /// <c>(InternalUserId, TenantId)</c> without requiring a reverse-lookup through
    /// <see cref="GetByInternalUserIdAsync"/>. Used by <c>InstallationStateGate</c> in Stage
    /// 5.1 to verify user-scoped targets identified by <c>AgentQuestion.TargetUserId</c>
    /// before proactive sends — this distinguishes "inactive" from "missing", which a plain
    /// <see cref="GetByInternalUserIdAsync"/> result cannot.
    /// </summary>
    Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct);

    /// <summary>
    /// Channel-scope active check. Mirrors <see cref="IsActiveAsync"/> for channel-scoped
    /// references. Used by <c>InstallationStateGate</c> in Stage 5.1 to verify channel
    /// targets before proactive sends.
    /// </summary>
    Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>
    /// Administrative cleanup — physically delete the personal-scope reference. Not used by
    /// the automated uninstall flow.
    /// </summary>
    Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>
    /// Administrative cleanup — physically delete the channel-scope reference. Not used by
    /// the automated uninstall flow.
    /// </summary>
    Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct);
}
