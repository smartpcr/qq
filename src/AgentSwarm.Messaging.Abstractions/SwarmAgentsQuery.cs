namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Query parameters for <see cref="ISwarmCommandBus.QueryAgentsAsync"/>.
/// Returns the <see cref="AgentInfo"/> snapshots matching the filter.
/// </summary>
/// <param name="TenantId">Tenant scope. Required.</param>
/// <param name="RoleFilter">
/// Optional agent role filter (e.g. <c>"Architect"</c>, <c>"Coder"</c>,
/// <c>"Tester"</c>). When non-null, only agents holding the matching role are
/// returned.
/// </param>
public sealed record SwarmAgentsQuery(
    string TenantId,
    string? RoleFilter);
