namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Query parameters for <see cref="ISwarmCommandBus.QueryStatusAsync"/>.
/// </summary>
public sealed record SwarmStatusQuery
{
    /// <summary>
    /// Required — scopes the query to the operator's workspace, derived
    /// from <c>OperatorBinding</c> (in <c>AgentSwarm.Messaging.Core</c>).
    /// </summary>
    public required string WorkspaceId { get; init; }

    /// <summary>
    /// When provided, narrows the result to a single task's state, assigned
    /// agent, and last activity. Used by <c>/status TASK-ID</c>.
    /// </summary>
    public string? TaskId { get; init; }
}
