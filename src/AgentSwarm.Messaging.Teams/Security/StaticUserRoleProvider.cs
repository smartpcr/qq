using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Default <see cref="IUserRoleProvider"/> that resolves roles from the
/// <see cref="RbacOptions.TenantRoleAssignments"/> map seeded by configuration.
/// Suitable for small deployments or as a fallback when an external provider has no
/// assignment for the user.
/// </summary>
public sealed class StaticUserRoleProvider : IUserRoleProvider
{
    private readonly IOptionsMonitor<RbacOptions> _options;

    /// <summary>Construct a <see cref="StaticUserRoleProvider"/>.</summary>
    /// <exception cref="ArgumentNullException">If <paramref name="options"/> is null.</exception>
    public StaticUserRoleProvider(IOptionsMonitor<RbacOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task<string?> GetRoleAsync(string tenantId, string aadObjectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_options.CurrentValue.ResolveRoleOrDefault(tenantId, aadObjectId));
    }
}
