namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Summary of agent / swarm status for rendering a status card via
/// <see cref="IAdaptiveCardRenderer.RenderStatusCard"/>. Aligned with
/// <c>architecture.md</c> §3.3 <c>AgentStatusSummary</c> field table.
/// </summary>
/// <param name="AgentId">The agent whose status is being reported.</param>
/// <param name="TaskId">Associated task (null for swarm-wide status).</param>
/// <param name="AgentName">Human-readable agent display name.</param>
/// <param name="CurrentState">Agent lifecycle state: <c>Idle</c>, <c>Working</c>, <c>Blocked</c>, <c>Paused</c>, <c>Error</c>.</param>
/// <param name="ActiveTaskCount">Number of tasks currently assigned.</param>
/// <param name="LastActivityAt">UTC time of the agent's most recent activity.</param>
/// <param name="ProgressPercent">Optional progress indicator (0-100). Null when not applicable.</param>
/// <param name="Summary">Free-text status description from the agent.</param>
/// <param name="CorrelationId">End-to-end trace ID.</param>
public sealed record AgentStatusSummary(
    string AgentId,
    string? TaskId,
    string AgentName,
    string CurrentState,
    int ActiveTaskCount,
    DateTimeOffset LastActivityAt,
    int? ProgressPercent,
    string Summary,
    string CorrelationId);
