namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Resolved authorized operator identity passed to command handlers after
/// allowlist / binding checks and (when needed) workspace disambiguation.
/// Flattened from a single <c>OperatorBinding</c> (in
/// <c>AgentSwarm.Messaging.Core</c>) so handlers can rely on it without
/// re-querying the registry.
/// </summary>
public sealed record AuthorizedOperator
{
    /// <summary>
    /// Stable internal identifier for the operator (typically the
    /// <c>OperatorBinding.Id</c> of the resolved binding — defined in
    /// <c>AgentSwarm.Messaging.Core</c>).
    /// </summary>
    public required Guid OperatorId { get; init; }

    public required string TenantId { get; init; }

    public required string WorkspaceId { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public required long TelegramUserId { get; init; }

    public required long TelegramChatId { get; init; }
}
