namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Records the mapping between a Telegram <c>(userId, chatId)</c> pair and a
/// tenant-scoped workspace identity. Multiple bindings may exist for the same
/// user across different workspaces; callers handle cardinality via the
/// allowlist / disambiguation rules defined in architecture.md §4.3.
/// </summary>
/// <remarks>
/// Lives in <c>AgentSwarm.Messaging.Core</c> per implementation-plan.md
/// Stage 1.3 and architecture.md §3.1. Consumed by <see cref="IOperatorRegistry"/>
/// (Core) and <see cref="AuthorizationResult"/> (Core).
/// </remarks>
public sealed record OperatorBinding
{
    public required Guid Id { get; init; }

    public required long TelegramUserId { get; init; }

    public required long TelegramChatId { get; init; }

    public required ChatType ChatType { get; init; }

    public required string OperatorAlias { get; init; }

    public required string TenantId { get; init; }

    public required string WorkspaceId { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public required DateTimeOffset RegisteredAt { get; init; }

    public bool IsActive { get; init; } = true;
}
