namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Discriminator for <see cref="SwarmCommand.CommandType"/>. Kept as a
/// string-backed value list rather than an open enum so connectors and
/// orchestrators can evolve the vocabulary independently; consumers should
/// reject unknown values defensively.
/// </summary>
public static class SwarmCommandType
{
    public const string CreateTask = "create-task";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Handoff = "handoff";
}

/// <summary>
/// Discriminator for <see cref="SwarmCommand.Scope"/>. Used by agent-scoped
/// commands (currently <c>/pause</c>, <c>/resume</c>) to distinguish a
/// single-agent directive (<see cref="Single"/>, with
/// <see cref="SwarmCommand.AgentId"/> populated) from a workspace-wide
/// fan-out (<see cref="All"/>, no <see cref="SwarmCommand.AgentId"/>).
/// Matches architecture.md §5 command table entries for
/// <c>/pause AGENT-ID</c> / <c>/pause all</c> and
/// <c>/resume AGENT-ID</c> / <c>/resume all</c>.
/// </summary>
public static class SwarmCommandScope
{
    /// <summary>The command targets a single agent named in <see cref="SwarmCommand.AgentId"/>.</summary>
    public const string Single = "single";

    /// <summary>The command fans out to every agent in <see cref="SwarmCommand.WorkspaceId"/>.</summary>
    public const string All = "all";
}

/// <summary>
/// Outbound directive from a human operator (via a messenger connector) to
/// the agent swarm orchestrator. Transport (in-process channel, broker,
/// gRPC) is intentionally not specified — see architecture.md §4.6.
/// </summary>
public sealed record SwarmCommand
{
    public required string CommandType { get; init; }

    /// <summary>Target task identifier, when the command is task-scoped.</summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// Target agent identifier, when the command is agent-scoped (e.g.
    /// <c>/pause AGENT-ID</c>, <c>/resume AGENT-ID</c>). <c>null</c> when
    /// the command fans out to every agent in <see cref="WorkspaceId"/>
    /// (<see cref="Scope"/> = <see cref="SwarmCommandScope.All"/>).
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Workspace the command applies to. Populated for agent-scoped
    /// commands so an <see cref="SwarmCommandScope.All"/> fan-out is
    /// bounded to the requesting operator's workspace (and so the swarm
    /// orchestrator can authorize the directive against the operator's
    /// resolved binding without re-querying the registry).
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// One of <see cref="SwarmCommandScope.Single"/> or
    /// <see cref="SwarmCommandScope.All"/>, for agent-scoped commands. Null
    /// for commands where scope is not meaningful (e.g.
    /// <see cref="SwarmCommandType.CreateTask"/>).
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Internal operator identifier — the <see cref="AuthorizedOperator.OperatorId"/>
    /// resolved by the inbound pipeline. Typed as <see cref="Guid"/> for
    /// consistency with the rest of the internal identity surface
    /// (<see cref="AuthorizedOperator.OperatorId"/>,
    /// <c>OperatorBinding.Id</c>, <c>TaskOversight.OperatorBindingId</c> —
    /// both defined in <c>AgentSwarm.Messaging.Core</c>).
    /// </summary>
    public required Guid OperatorId { get; init; }

    /// <summary>Command-type-specific payload (e.g. <c>/ask</c> body text).</summary>
    public string? Payload { get; init; }

    private readonly string _correlationId = null!;

    /// <summary>
    /// Trace identifier — must be non-null, non-empty, non-whitespace per
    /// the "All messages include trace/correlation ID" acceptance criterion.
    /// </summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }
}
