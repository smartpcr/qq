namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Per-agent snapshot returned by <see cref="ISwarmCommandBus.QueryAgentsAsync"/>
/// and rendered into Discord embeds (agent identity card per architecture.md
/// Section 4.9 "Agent identity rendering").
/// </summary>
/// <param name="AgentId">Stable agent identifier.</param>
/// <param name="Role">Logical agent role (e.g. <c>"Architect"</c>, <c>"Coder"</c>, <c>"Tester"</c>).</param>
/// <param name="CurrentTask">
/// Short description of the task the agent is currently working on, or
/// <see langword="null"/> when idle.
/// </param>
/// <param name="ConfidenceScore">
/// Self-reported confidence, in the inclusive range <c>[0.0, 1.0]</c>. Connectors
/// render this as a discrete progress-bar emoji sequence.
/// </param>
/// <param name="BlockingQuestion">
/// Identifier of the open <see cref="AgentQuestion"/> that is blocking the
/// agent, or <see langword="null"/> when the agent is not stalled. Connectors
/// surface this as a "blocked" indicator on the agent identity card.
/// </param>
public sealed record AgentInfo(
    string AgentId,
    string Role,
    string? CurrentTask,
    double ConfidenceScore,
    string? BlockingQuestion);
