namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Deny-by-default stub implementation of <see cref="IIdentityResolver"/> used as the
/// pre-Stage 5.1 placeholder. Returns <c>null</c> for every AAD object ID, which triggers the
/// unmapped-user rejection flow (<c>architecture.md</c> §6.4.2) and prevents any inbound
/// activity from succeeding until a real resolver (for example, <c>EntraIdentityResolver</c>)
/// is registered via DI override in Stage 5.1.
/// </summary>
/// <remarks>
/// Registered in DI by Stage 2.1 so that <c>TeamsSwarmActivityHandler</c> can be constructed
/// before the concrete <c>EntraIdentityResolver</c> exists.
/// </remarks>
public sealed class DefaultDenyIdentityResolver : IIdentityResolver
{
    /// <inheritdoc />
    public Task<UserIdentity?> ResolveAsync(string aadObjectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<UserIdentity?>(null);
    }
}
