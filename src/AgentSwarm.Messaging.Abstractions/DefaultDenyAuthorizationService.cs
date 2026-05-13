namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Deny-by-default stub implementation of <see cref="IUserAuthorizationService"/> used as the
/// pre-Stage 5.1 placeholder. Returns
/// <see cref="AuthorizationResult"/> with <c>IsAuthorized = false</c> for every request,
/// enforcing a minimal deny-all policy until a real RBAC implementation (for example,
/// <c>RbacAuthorizationService</c>) is registered via DI override in Stage 5.1.
/// </summary>
public sealed class DefaultDenyAuthorizationService : IUserAuthorizationService
{
    /// <inheritdoc />
    public Task<AuthorizationResult> AuthorizeAsync(
        string tenantId,
        string userId,
        string command,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AuthorizationResult(
            IsAuthorized: false,
            UserRole: null,
            RequiredRole: null));
    }
}
