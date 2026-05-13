namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Query parameters for <see cref="ISwarmCommandBus.QueryAgentsAsync"/>.
/// </summary>
public sealed record SwarmAgentsQuery
{
    /// <summary>Required — workspace scope.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Optional free-text agent name/role filter.</summary>
    public string? Filter { get; init; }
}
