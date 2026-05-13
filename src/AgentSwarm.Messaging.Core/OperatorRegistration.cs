namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Value object accepted by <see cref="IOperatorRegistry.RegisterAsync"/>;
/// carries every field required to construct a new
/// <see cref="OperatorBinding"/>. The <c>/start</c> handler builds this from
/// the Telegram <c>Update</c> (for user/chat/type) and the
/// <c>Telegram:UserTenantMappings</c> configuration entry (for tenant,
/// workspace, roles, and alias).
/// </summary>
public sealed record OperatorRegistration
{
    public required long TelegramUserId { get; init; }

    public required long TelegramChatId { get; init; }

    public required ChatType ChatType { get; init; }

    public required string TenantId { get; init; }

    public required string WorkspaceId { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>Operator handle, e.g. <c>@alice</c>.</summary>
    public required string OperatorAlias { get; init; }
}
