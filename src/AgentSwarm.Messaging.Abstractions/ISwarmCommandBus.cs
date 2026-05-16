namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Bidirectional bus between messenger connectors and the swarm orchestrator.
/// Exposes the four interaction modes a connector needs: publishing
/// human-originated commands, publishing operator decisions on agent
/// questions, querying current swarm state for status responses, and
/// subscribing to push events for proactive operator notifications. See
/// architecture.md Section 4.6.
/// </summary>
public interface ISwarmCommandBus
{
    /// <summary>
    /// Publishes a slash-command originated <see cref="SwarmCommand"/> to the
    /// orchestrator (e.g. <c>/agent ask</c>, <c>/agent approve</c>,
    /// <c>/agent assign</c>).
    /// </summary>
    /// <param name="command">The validated command.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishCommandAsync(SwarmCommand command, CancellationToken ct);

    /// <summary>
    /// Publishes a <see cref="HumanDecisionEvent"/> resolving a previously asked
    /// <see cref="AgentQuestion"/>.
    /// </summary>
    /// <param name="decision">The operator's decision.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishHumanDecisionAsync(HumanDecisionEvent decision, CancellationToken ct);

    /// <summary>
    /// Returns aggregated swarm status for the given query.
    /// </summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct);

    /// <summary>
    /// Returns per-agent snapshots matching the given query.
    /// </summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct);

    /// <summary>
    /// Subscribes to the orchestrator's push event stream for the given tenant.
    /// The returned async sequence yields a <see cref="SwarmEvent"/> per
    /// orchestrator emission until the caller cancels via <paramref name="ct"/>
    /// or the orchestrator closes the stream.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<SwarmEvent> SubscribeAsync(string tenantId, CancellationToken ct);
}
