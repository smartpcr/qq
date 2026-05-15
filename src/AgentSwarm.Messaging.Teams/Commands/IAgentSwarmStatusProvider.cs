using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Read-only query port that <see cref="StatusCommandHandler"/> uses to fetch agent /
/// swarm status from the orchestrator boundary (<c>architecture.md</c> §2.14). The Teams
/// project ships a default <see cref="NullAgentSwarmStatusProvider"/> implementation that
/// returns no agents — the host's orchestrator integration wires its concrete adapter via
/// DI override before the bot starts taking traffic.
/// </summary>
/// <remarks>
/// <para>
/// The contract is intentionally narrow — <c>StatusCommandHandler</c> needs <em>only</em>
/// the data required to render the status Adaptive Card via
/// <see cref="Cards.IAdaptiveCardRenderer.RenderStatusCard"/>. Richer queries
/// (per-task drill-down, swarm topology) live on separate orchestrator-facing ports and
/// are out of scope for Stage 3.2.
/// </para>
/// <para>
/// The query is scoped by the resolved user identity so RBAC-aware providers can filter
/// the result set per-user (for example, hiding agents the user has no read permission
/// on). The default implementation ignores the identity since it returns no agents.
/// </para>
/// </remarks>
public interface IAgentSwarmStatusProvider
{
    /// <summary>
    /// Return the current status summary list for the swarm visible to
    /// <paramref name="resolvedIdentity"/>. An empty list signals that no agents are
    /// active in the user's scope; the handler renders a friendly "no active agents"
    /// card in that case.
    /// </summary>
    /// <param name="resolvedIdentity">The internal user identity of the requester.</param>
    /// <param name="tenantId">The Entra ID tenant of the requester.</param>
    /// <param name="correlationId">End-to-end trace ID propagated from the command.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    Task<IReadOnlyList<Cards.AgentStatusSummary>> GetStatusAsync(
        UserIdentity resolvedIdentity,
        string tenantId,
        string correlationId,
        CancellationToken ct);
}

/// <summary>
/// Default <see cref="IAgentSwarmStatusProvider"/> registered when the host has not wired a
/// real orchestrator integration. Returns an empty list so <see cref="StatusCommandHandler"/>
/// renders a "Status integration not configured" card without crashing. Hosts SHOULD
/// override this registration via DI before going to production — the default exists only
/// to keep development / unit-test environments runnable.
/// </summary>
public sealed class NullAgentSwarmStatusProvider : IAgentSwarmStatusProvider
{
    /// <inheritdoc />
    public Task<IReadOnlyList<Cards.AgentStatusSummary>> GetStatusAsync(
        UserIdentity resolvedIdentity,
        string tenantId,
        string correlationId,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Cards.AgentStatusSummary>>(Array.Empty<Cards.AgentStatusSummary>());
    }
}
