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
