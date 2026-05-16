namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Aggregated swarm status for a single <c>/agent status</c> query response.
/// Kept intentionally compact (counts only) — per-agent detail is exposed via
/// <see cref="ISwarmCommandBus.QueryAgentsAsync"/> and <see cref="AgentInfo"/>.
/// </summary>
/// <param name="TotalAgents">Total agents matching the originating <see cref="SwarmStatusQuery"/>.</param>
/// <param name="ActiveTasks">Number of agents currently executing a task.</param>
/// <param name="BlockedCount">
/// Number of agents stalled on a <see cref="AgentInfo.BlockingQuestion"/>
/// awaiting human input.
/// </param>
public sealed record SwarmStatusSummary(
    int TotalAgents,
    int ActiveTasks,
    int BlockedCount);
