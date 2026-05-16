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
/// Users must hold at least one matching role. Defensively copied at construction
/// to prevent post-hoc mutation; exposed as <see cref="IReadOnlyList{T}"/>.
/// </param>
/// <param name="CommandRestrictions">
/// Optional per-command role overrides. The key is the subcommand name
/// (e.g. <c>"approve"</c>), the value is the set of role ids permitted to invoke
/// it. When <see langword="null"/>, <see cref="AllowedRoleIds"/> applies uniformly.
/// Defensively copied at construction (both the outer dictionary and each value list).
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
    IReadOnlyList<ulong> AllowedRoleIds,
    IReadOnlyDictionary<string, IReadOnlyList<ulong>>? CommandRestrictions,
    DateTimeOffset RegisteredAt,
    bool IsActive)
{
    private readonly IReadOnlyList<ulong> _allowedRoleIds = CopyRolesOrThrow(AllowedRoleIds);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ulong>>? _commandRestrictions =
        CopyRestrictions(CommandRestrictions);

    /// <inheritdoc cref="GuildBinding(Guid, ulong, ulong, ChannelPurpose, string, string, IReadOnlyList{ulong}, IReadOnlyDictionary{string, IReadOnlyList{ulong}}?, DateTimeOffset, bool)"/>
    public IReadOnlyList<ulong> AllowedRoleIds
    {
        get => _allowedRoleIds;
        init => _allowedRoleIds = CopyRolesOrThrow(value);
    }

    /// <inheritdoc cref="GuildBinding(Guid, ulong, ulong, ChannelPurpose, string, string, IReadOnlyList{ulong}, IReadOnlyDictionary{string, IReadOnlyList{ulong}}?, DateTimeOffset, bool)"/>
    public IReadOnlyDictionary<string, IReadOnlyList<ulong>>? CommandRestrictions
    {
        get => _commandRestrictions;
        init => _commandRestrictions = CopyRestrictions(value);
    }

    private static IReadOnlyList<ulong> CopyRolesOrThrow(IReadOnlyList<ulong>? value)
        => value is null
            ? throw new ArgumentNullException(nameof(AllowedRoleIds))
            : value.ToArray();

    private static IReadOnlyDictionary<string, IReadOnlyList<ulong>>? CopyRestrictions(
        IReadOnlyDictionary<string, IReadOnlyList<ulong>>? value)
    {
        if (value is null)
        {
            return null;
        }

        var copy = new Dictionary<string, IReadOnlyList<ulong>>(value.Count);
        foreach (var kv in value)
        {
            copy[kv.Key] = kv.Value is null
                ? Array.Empty<ulong>()
                : kv.Value.ToArray();
        }

        return copy;
    }
}
