namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Enforces RBAC permissions for canonical messenger commands. Aligned with
/// <c>architecture.md</c> §4.10 and the role-to-command matrix in §5.2.
/// </summary>
public interface IUserAuthorizationService
{
    /// <summary>
    /// Check whether the user identified by <paramref name="tenantId"/> /
    /// <paramref name="userId"/> is permitted to execute <paramref name="command"/>.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant of the calling user.</param>
    /// <param name="userId">Internal user identifier (NOT the AAD object ID).</param>
    /// <param name="command">Canonical command vocabulary value (for example, <c>approve</c>, <c>reject</c>, <c>agent ask</c>).</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>
    /// An <see cref="AuthorizationResult"/> describing whether the command is permitted and,
    /// when not, the role required to execute it.
    /// </returns>
    Task<AuthorizationResult> AuthorizeAsync(
        string tenantId,
        string userId,
        string command,
        CancellationToken ct);
}
