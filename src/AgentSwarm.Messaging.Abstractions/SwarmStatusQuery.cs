namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Query parameters for <see cref="ISwarmCommandBus.QueryStatusAsync"/>. Returns
/// a <see cref="SwarmStatusSummary"/> aggregated over the matching tenant (and
/// optionally a single agent).
/// </summary>
/// <param name="TenantId">Tenant scope. Required.</param>
/// <param name="AgentId">
/// Optional agent filter. When non-null, the summary is scoped to that single
/// agent rather than the full tenant population.
/// </param>
public sealed record SwarmStatusQuery(
    string TenantId,
    string? AgentId);
