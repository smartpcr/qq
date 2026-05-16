namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Provider contract that resolves the RBAC role assigned to a specific AAD-mapped user.
/// Operators plug in an LDAP, Microsoft Graph, SCIM, or static implementation; the default
/// <see cref="StaticUserRoleProvider"/> resolves roles from
/// <see cref="RbacOptions.TenantRoleAssignments"/>.
/// </summary>
/// <remarks>
/// Returning <c>null</c> indicates the provider has no opinion for the supplied user;
/// <see cref="RbacAuthorizationService"/> then falls back to the static assignment map and
/// finally to <see cref="RbacOptions.DefaultRole"/>.
/// </remarks>
public interface IUserRoleProvider
{
    /// <summary>
    /// Resolve the RBAC role for the supplied user.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="aadObjectId">User AAD object ID (Entra <c>oid</c> claim).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role name (must match a key in <see cref="RbacOptions.RoleCommands"/>),
    /// or <c>null</c> when the provider has no assignment for the user.</returns>
    Task<string?> GetRoleAsync(string tenantId, string aadObjectId, CancellationToken cancellationToken);
}
