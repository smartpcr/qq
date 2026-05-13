namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Summary view of the swarm or a single task returned by
/// <see cref="ISwarmCommandBus.QueryStatusAsync"/>. The shape is intentionally
/// permissive so the orchestrator implementation can grow without breaking
/// the messenger connectors.
/// </summary>
public sealed record SwarmStatusSummary
{
    public required string WorkspaceId { get; init; }

    /// <summary>Aggregate state (e.g. <c>running</c>, <c>idle</c>).</summary>
    public required string State { get; init; }

    public int ActiveAgentCount { get; init; }

    public int PendingTaskCount { get; init; }

    /// <summary>
    /// When <see cref="SwarmStatusQuery.TaskId"/> was supplied, identifies
    /// the task whose state is being reported.
    /// </summary>
    public string? TaskId { get; init; }

    /// <summary>Last-activity timestamp, when available.</summary>
    public DateTimeOffset? LastActivityAt { get; init; }

    /// <summary>Free-form, presentation-ready text for rendering in chat.</summary>
    public string? DisplayText { get; init; }
}

/// <summary>
/// Minimal description of an agent returned by
/// <see cref="ISwarmCommandBus.QueryAgentsAsync"/>.
/// </summary>
public sealed record AgentInfo
{
    public required string AgentId { get; init; }

    public required string Role { get; init; }

    public required string State { get; init; }

    public string? CurrentTaskId { get; init; }

    public DateTimeOffset? LastHeartbeatAt { get; init; }
}
