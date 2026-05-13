namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Port between messenger connectors and the agent swarm orchestrator. All
/// outbound commands and queries — and inbound subscription events — flow
/// through this single interface; the underlying transport (in-process
/// channel, broker, gRPC stream) is intentionally not specified here. See
/// architecture.md §4.6.
/// </summary>
public interface ISwarmCommandBus
{
    Task PublishCommandAsync(SwarmCommand command, CancellationToken ct);

    /// <summary>
    /// Publish a strongly-typed <see cref="HumanDecisionEvent"/> derived from
    /// an inline-keyboard approval/rejection callback (or its follow-up text
    /// reply when the action <see cref="HumanAction.RequiresComment"/>). This
    /// is the contract that satisfies the story acceptance criterion
    /// "Approval/rejection buttons are converted into strongly typed agent
    /// events" (architecture.md §10.2). <see cref="ICallbackHandler"/>
    /// implementations construct the event and invoke this method;
    /// orchestrator implementations subscribe via the swarm's own event
    /// dispatch mechanism (out of scope for Stage 1.3).
    /// </summary>
    Task PublishHumanDecisionAsync(HumanDecisionEvent decision, CancellationToken ct);

    Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct);

    Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct);

    /// <summary>
    /// Subscribe to swarm-originated events for the supplied tenant. The
    /// connector calls this at startup for each active tenant and processes
    /// the resulting stream — rendering questions as inline-keyboard
    /// messages, alerts as priority text, and status updates as
    /// informational messages.
    /// </summary>
    IAsyncEnumerable<SwarmEvent> SubscribeAsync(string tenantId, CancellationToken ct);
}
