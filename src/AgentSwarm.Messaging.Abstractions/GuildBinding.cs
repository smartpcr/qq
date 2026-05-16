using System.Collections.ObjectModel;

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
/// Users must hold at least one matching role. The caller-supplied collection
/// is defensively copied at construction into a <see cref="ReadOnlyCollection{T}"/>;
/// the value exposed to consumers cannot be downcast to <c>ulong[]</c>,
/// <see cref="List{T}"/>, or any other mutable <see cref="IList{T}"/>
/// implementation, and mutating members of the <see cref="IList{T}"/> view
/// throw <see cref="NotSupportedException"/>.
/// </param>
/// <param name="CommandRestrictions">
/// Optional per-command role overrides. The key is the subcommand name
/// (e.g. <c>"approve"</c>), the value is the set of role ids permitted to invoke
/// it. When <see langword="null"/>, <see cref="AllowedRoleIds"/> applies uniformly.
/// The caller-supplied dictionary is defensively copied at construction into a
/// <see cref="ReadOnlyDictionary{TKey, TValue}"/> with each value wrapped in a
/// <see cref="ReadOnlyCollection{T}"/>, so neither the outer dictionary nor any
/// value collection can be downcast back to a mutable form. Null value
/// collections are <strong>rejected</strong> with <see cref="ArgumentException"/>
/// (rather than silently coerced to empty) to surface malformed configuration
/// or JSON payloads.
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
    private readonly IReadOnlyList<ulong> _allowedRoleIds = ToImmutableRoles(AllowedRoleIds);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ulong>>? _commandRestrictions =
        ToImmutableRestrictions(CommandRestrictions);

    /// <inheritdoc cref="GuildBinding(Guid, ulong, ulong, ChannelPurpose, string, string, IReadOnlyList{ulong}, IReadOnlyDictionary{string, IReadOnlyList{ulong}}?, DateTimeOffset, bool)"/>
    public IReadOnlyList<ulong> AllowedRoleIds
    {
        get => _allowedRoleIds;
        init => _allowedRoleIds = ToImmutableRoles(value);
    }

    /// <inheritdoc cref="GuildBinding(Guid, ulong, ulong, ChannelPurpose, string, string, IReadOnlyList{ulong}, IReadOnlyDictionary{string, IReadOnlyList{ulong}}?, DateTimeOffset, bool)"/>
    public IReadOnlyDictionary<string, IReadOnlyList<ulong>>? CommandRestrictions
    {
        get => _commandRestrictions;
        init => _commandRestrictions = ToImmutableRestrictions(value);
    }

    private static IReadOnlyList<ulong> ToImmutableRoles(IReadOnlyList<ulong>? value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(AllowedRoleIds));
        }

        // Snapshot to a private array, then wrap as ReadOnlyCollection so callers
        // cannot downcast the IReadOnlyList<ulong> back to ulong[] / List<ulong>
        // and mutate the binding's allowed role set.
        return new ReadOnlyCollection<ulong>(value.ToArray());
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ulong>>? ToImmutableRestrictions(
        IReadOnlyDictionary<string, IReadOnlyList<ulong>>? value)
    {
        if (value is null)
        {
            return null;
        }

        var copy = new Dictionary<string, IReadOnlyList<ulong>>(value.Count, StringComparer.Ordinal);
        foreach (var kv in value)
        {
            if (kv.Value is null)
            {
                // Surface malformed config / JSON loudly. The previous silent
                // coercion to Array.Empty<ulong>() hid bugs where a producer
                // forgot to populate the per-command role list.
                throw new ArgumentException(
                    $"CommandRestrictions['{kv.Key}'] must not be null; use an empty collection to explicitly grant no roles.",
                    nameof(CommandRestrictions));
            }

            copy[kv.Key] = new ReadOnlyCollection<ulong>(kv.Value.ToArray());
        }

        // Wrap outer dict so callers cannot downcast back to Dictionary<,> and
        // edit the role overrides after the binding is constructed.
        return new ReadOnlyDictionary<string, IReadOnlyList<ulong>>(copy);
    }
}

