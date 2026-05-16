namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Push event delivered to subscribers of <see cref="ISwarmCommandBus.SubscribeAsync"/>.
/// Emitted by the orchestrator whenever swarm state changes in a way the
/// connector should surface to operators (agent claims a task, agent reports
/// progress, agent blocks on a question, agent fails). The connector inspects
/// <see cref="EventType"/> and <see cref="AgentId"/> to choose a routing channel
/// (Control / Alert / Workstream per <see cref="ChannelPurpose"/>) before
/// rendering.
/// </summary>
/// <param name="EventType">
/// Logical event class (e.g. <c>"AgentStarted"</c>, <c>"TaskClaimed"</c>,
/// <c>"ProgressUpdate"</c>, <c>"AgentBlocked"</c>, <c>"TaskCompleted"</c>,
/// <c>"AgentFailed"</c>). Free-form string so the orchestrator and connectors
/// can extend the vocabulary without bumping the bus contract; consumers that
/// do not recognise a type should log + skip rather than throw.
/// </param>
/// <param name="AgentId">Originating agent identifier.</param>
/// <param name="Payload">
/// JSON-serialized event-specific payload. Kept as an opaque string so the
/// shared contract does not need to know every event shape; consumers
/// deserialize per <see cref="EventType"/>.
/// </param>
/// <param name="CorrelationId">End-to-end trace identifier.</param>
/// <param name="Timestamp">When the event was emitted by the orchestrator.</param>
public sealed record SwarmEvent(
    string EventType,
    string AgentId,
    string Payload,
    string CorrelationId,
    DateTimeOffset Timestamp);
