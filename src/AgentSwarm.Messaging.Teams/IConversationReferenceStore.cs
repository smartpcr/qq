namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persistence contract for <see cref="TeamsConversationReference"/> rows. Defined in
/// <c>AgentSwarm.Messaging.Teams</c> (not Abstractions) because the host assembly is the
/// canonical owner and the dual-key model surfaces Teams-specific identity vocabulary
/// (<see cref="TeamsConversationReference.AadObjectId"/>). Aligned with
/// <c>architecture.md</c> §4.2 contract.
/// </summary>
/// <remarks>
/// <para>
/// The contract supports two parallel lookup paths so the connector can serve both inbound
/// activity routing (keyed by Teams-native <c>AadObjectId</c>) and proactive sends triggered
/// by the orchestrator (keyed by the platform-agnostic <c>InternalUserId</c>).
/// </para>
/// <para>
/// Inactive references are excluded from the lookup family (<see cref="GetAsync"/>,
/// <see cref="GetByAadObjectIdAsync"/>, <see cref="GetByInternalUserIdAsync"/>,
/// <see cref="GetByChannelIdAsync"/>, <see cref="GetAllActiveAsync"/>) so callers cannot
/// distinguish "missing" from "uninstalled" — that distinction is recoverable only via the
/// explicit <see cref="IsActiveAsync"/>/<see cref="IsActiveByChannelAsync"/>/
/// <see cref="IsActiveByInternalUserIdAsync"/> probes, which return <see langword="false"/>
/// for both cases. The Stage 5.1 installation-state gate uses those probes to verify a
/// target before issuing a proactive send.
/// </para>
/// </remarks>
public interface IConversationReferenceStore
{
    /// <summary>Insert a new reference, or update the existing row by primary key.</summary>
    Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct);

    /// <summary>Return the active reference matching the supplied primary key, or null.</summary>
    Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>Return every active reference in the store.</summary>
    Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(CancellationToken ct);

    /// <summary>Lookup by Teams-native AAD identity key. Returns null if missing or inactive.</summary>
    Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>Lookup by orchestrator internal user ID for proactive routing. Returns null if missing or inactive.</summary>
    Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct);

    /// <summary>Lookup by Teams channel ID. Returns null if missing or inactive.</summary>
    Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>Set <c>IsActive = false</c> on the user-scoped reference identified by
    /// <paramref name="aadObjectId"/>. Used on personal-chat uninstall.</summary>
    Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>Set <c>IsActive = false</c> on the channel-scoped reference identified by
    /// <paramref name="channelId"/>. Used on team-channel uninstall per <c>architecture.md</c> §4.2.</summary>
    Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>Return <see langword="true"/> when an active reference exists for the AAD
    /// identity. Stage 5.1's <c>InstallationStateGate</c> uses this probe to verify a
    /// user-scoped target before issuing a proactive send.</summary>
    Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>Return <see langword="true"/> when an active reference exists for the
    /// orchestrator's internal user ID. This direct probe avoids the contract mismatch where
    /// <see cref="GetByInternalUserIdAsync"/> returns only active references, making it
    /// impossible to distinguish "inactive" from "missing".</summary>
    Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct);

    /// <summary>Return <see langword="true"/> when an active reference exists for the channel
    /// identity. Mirrors <see cref="IsActiveAsync"/> for channel-scoped references.</summary>
    Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct);

    /// <summary>Permanently delete the row identified by the AAD primary key. Administrative
    /// cleanup only; ordinary uninstall flows use <see cref="MarkInactiveAsync"/>.</summary>
    Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct);

    /// <summary>Permanently delete the row identified by the channel primary key.
    /// Administrative cleanup only; ordinary uninstall flows use
    /// <see cref="MarkInactiveByChannelAsync"/>.</summary>
    Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct);
}
