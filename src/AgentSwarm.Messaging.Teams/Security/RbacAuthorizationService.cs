using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Role-scoped <see cref="IUserAuthorizationService"/> that consults
/// <see cref="RbacOptions"/> and the application-supplied
/// <see cref="IUserRoleProvider"/> to authorize canonical commands. Concrete
/// implementation of the contract defined in Stage 1.2; replaces the
/// <see cref="DefaultDenyAuthorizationService"/> registered as the Stage 2.1 stub.
/// Aligned with <c>architecture.md</c> §2.6 and §5.2 and
/// <c>implementation-plan.md</c> §5.1 step 2.
/// </summary>
/// <remarks>
/// <para>
/// Authorization order, per command-time resolution in <c>architecture.md</c> §5.2:
/// </para>
/// <list type="number">
///   <item><description>Resolve the AAD object ID's role via
///   <see cref="IUserRoleProvider.GetRoleAsync"/> — operators inject an LDAP/Graph/SCIM
///   provider here. Falls back to the static
///   <see cref="RbacOptions.TenantRoleAssignments"/> map when the provider returns
///   <c>null</c>, then to <see cref="RbacOptions.DefaultRole"/>.</description></item>
///   <item><description>Look up the role's permitted-command set in
///   <see cref="RbacOptions.RoleCommands"/>. If the user's role is unconfigured or the
///   command is not in its set, return <see cref="AuthorizationResult"/> with
///   <see cref="AuthorizationResult.IsAuthorized"/> = <c>false</c> and
///   <see cref="AuthorizationResult.RequiredRole"/> populated via
///   <see cref="RbacOptions.FindRequiredRole"/> so the access-denied card can explain the
///   missing role.</description></item>
/// </list>
/// <para>
/// <b>userId contract:</b> the caller supplies the user's AAD object ID (the same value
/// captured from <c>Activity.From.AadObjectId</c>) — the activity handler intentionally
/// passes the AAD object ID rather than the internal user ID so the role lookup happens
/// against the identity native to Entra and the role provider does not have to perform an
/// additional internal→AAD reverse mapping. Internal user IDs are still used for proactive
/// routing via <see cref="UserIdentity.InternalUserId"/>.
/// </para>
/// </remarks>
public sealed class RbacAuthorizationService : IUserAuthorizationService
{
    private readonly IOptionsMonitor<RbacOptions> _options;
    private readonly IUserRoleProvider _roleProvider;
    private readonly ILogger<RbacAuthorizationService> _logger;

    /// <summary>
    /// Construct a new <see cref="RbacAuthorizationService"/>.
    /// </summary>
    /// <param name="options">Options monitor for <see cref="RbacOptions"/> so configuration changes hot-reload.</param>
    /// <param name="roleProvider">Application-supplied role-lookup provider (LDAP, Graph, SCIM, static).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is <c>null</c>.</exception>
    public RbacAuthorizationService(
        IOptionsMonitor<RbacOptions> options,
        IUserRoleProvider roleProvider,
        ILogger<RbacAuthorizationService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _roleProvider = roleProvider ?? throw new ArgumentNullException(nameof(roleProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AuthorizationResult> AuthorizeAsync(
        string tenantId,
        string userId,
        string command,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(command))
        {
            return new AuthorizationResult(
                IsAuthorized: false,
                UserRole: null,
                RequiredRole: null);
        }

        var options = _options.CurrentValue;
        var role = await _roleProvider.GetRoleAsync(tenantId, userId, ct).ConfigureAwait(false)
                   ?? options.ResolveRoleOrDefault(tenantId, userId);

        if (string.IsNullOrEmpty(role))
        {
            _logger.LogWarning(
                "RBAC reject: user {UserId} in tenant {TenantId} has no role assignment for command '{Command}'.",
                userId,
                tenantId,
                command);
            return new AuthorizationResult(
                IsAuthorized: false,
                UserRole: null,
                RequiredRole: options.FindRequiredRole(command));
        }

        if (options.IsCommandPermitted(role, command))
        {
            return new AuthorizationResult(
                IsAuthorized: true,
                UserRole: role,
                RequiredRole: null);
        }

        var requiredRole = options.FindRequiredRole(command);
        _logger.LogWarning(
            "RBAC reject: user {UserId} (role {UserRole}) lacks role {RequiredRole} for command '{Command}' in tenant {TenantId}.",
            userId,
            role,
            requiredRole,
            command,
            tenantId);
        return new AuthorizationResult(
            IsAuthorized: false,
            UserRole: role,
            RequiredRole: requiredRole);
    }
}
