namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Links a messenger channel (currently a Discord guild/channel) to a swarm
/// tenant/workspace and the authorization roles allowed to drive it. Shared
/// across Core (used by <c>AuthorizationResult</c>), Discord (used by
/// <c>IGuildRegistry</c>), and Persistence (used by the EF entity configuration).
/// See architecture.md Section 3.1 for the canonical schema.
/// </summary>
/// <param name="Id">Surrogate primary key.</param>
/// <param name="GuildId">Discord guild (server) snowflake id.</param>
/// <param name="ChannelId">Discord channel snowflake id.</param>
/// <param name="ChannelPurpose">
/// Logical purpose driving which message types are routed to this channel.
/// </param>
/// <param name="TenantId">Swarm tenant the binding belongs to.</param>
/// <param name="WorkspaceId">Workspace within the tenant.</param>
/// <param name="AllowedRoleIds">
/// Discord role snowflake ids authorized to issue commands in this channel.
/// Users must hold at least one matching role.
/// </param>
/// <param name="CommandRestrictions">
/// Optional per-command role overrides. The key is the subcommand name
/// (e.g. <c>"approve"</c>), the value is the set of role ids permitted to invoke
/// it. When <see langword="null"/>, <see cref="AllowedRoleIds"/> applies uniformly.
/// </param>
/// <param name="RegisteredAt">When the binding was created.</param>
/// <param name="IsActive">Soft-disable flag (binding kept for audit history).</param>
public sealed record GuildBinding(
    Guid Id,
    ulong GuildId,
    ulong ChannelId,
    ChannelPurpose ChannelPurpose,
    string TenantId,
    string WorkspaceId,
    ulong[] AllowedRoleIds,
    IReadOnlyDictionary<string, ulong[]>? CommandRestrictions,
    DateTimeOffset RegisteredAt,
    bool IsActive);
