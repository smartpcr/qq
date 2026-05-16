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

    /// <summary>
    /// Human-readable operator handle (e.g. <c>@alice</c>) carried over
    /// from the underlying <c>OperatorBinding.OperatorAlias</c>. Used by
    /// Stage 3.2's <c>HandoffCommandHandler</c> when recording who
    /// initiated an oversight transfer on the persisted
    /// <c>TaskOversight.AssignedBy</c> field (per architecture.md §5.5
    /// "Full oversight transfer (Decided)") so the audit trail names
    /// operators by the alias the chat surfaces, not by an internal
    /// <see cref="OperatorId"/> GUID a reviewer cannot read. Defaults
    /// to <see cref="string.Empty"/> so existing call sites that omit
    /// the field — e.g. pre-Stage 3.2 contract tests — continue to
    /// compile; production construction is via
    /// <c>TelegramUpdatePipeline.ProcessAsync</c>, which always copies
    /// the binding's alias.
    /// </summary>
    public string OperatorAlias { get; init; } = string.Empty;
}
