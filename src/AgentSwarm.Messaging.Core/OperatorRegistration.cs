namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Value object accepted by <see cref="IOperatorRegistry.RegisterAsync"/>
/// (single row) and <see cref="IOperatorRegistry.RegisterManyAsync"/>
/// (atomic batch — preferred by the <c>/start</c> multi-workspace
/// onboarding path in Stage 3.4 iter-3); carries every field required
/// to construct a new <see cref="OperatorBinding"/>. The <c>/start</c>
/// handler builds a batch of these from the Telegram <c>Update</c>
/// (for user/chat/type) and each entry under the user's
/// <c>Telegram:UserTenantMappings</c> key (for tenant, workspace,
/// roles, and alias).
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
