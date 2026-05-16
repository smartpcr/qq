using System.Collections.ObjectModel;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Strongly typed configuration for <see cref="RbacAuthorizationService"/>. Maps RBAC role
/// names to the canonical messenger commands a holder of that role is permitted to execute,
/// and maps user AAD object IDs (per-tenant) to a single role assignment. Aligned with
/// <c>architecture.md</c> §5.2 and the implementation-plan §5.1 step "Create
/// <c>RbacOptions</c> configuration class mapping Teams user roles to allowed commands".
/// </summary>
/// <remarks>
/// <para>
/// The defaults populated by <see cref="WithDefaultRoleMatrix"/> mirror the role-to-command
/// matrix in <c>architecture.md</c> §5.2:
/// </para>
/// <list type="bullet">
///   <item><description><c>Operator</c> — every canonical command.</description></item>
///   <item><description><c>Approver</c> — <c>approve</c>, <c>reject</c>, <c>agent status</c>.</description></item>
///   <item><description><c>Viewer</c> — <c>agent status</c> only.</description></item>
/// </list>
/// <para>
/// Role name comparisons are <see cref="StringComparer.OrdinalIgnoreCase"/>; command verb
/// comparisons are <see cref="StringComparer.Ordinal"/> because the dispatcher already
/// lowercases every verb against the canonical vocabulary defined in
/// <c>TeamsSwarmActivityHandler.KnownCommandVerbs</c>.
/// </para>
/// </remarks>
public sealed class RbacOptions
{
    /// <summary>Configuration section bound from <c>appsettings.json</c> in <c>Program.cs</c>.</summary>
    public const string SectionName = "Teams:Rbac";

    /// <summary>Canonical role name granted every command.</summary>
    public const string OperatorRole = "Operator";

    /// <summary>Canonical role name granted approve / reject / agent status.</summary>
    public const string ApproverRole = "Approver";

    /// <summary>Canonical role name granted only read-only status.</summary>
    public const string ViewerRole = "Viewer";

    private static readonly IReadOnlyList<string> AllCommands = new[]
    {
        "agent ask",
        "agent status",
        "approve",
        "reject",
        "escalate",
        "pause",
        "resume",
    };

    /// <summary>
    /// Mapping from role name (case-insensitive) to the set of canonical commands the role
    /// is permitted to execute. Commands are matched ordinally because the activity handler
    /// always supplies the canonical lower-case verb.
    /// </summary>
    public IDictionary<string, IReadOnlyCollection<string>> RoleCommands { get; }
        = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mapping from tenant ID to AAD-object-ID → role-name assignments. The role name must
    /// match a key in <see cref="RoleCommands"/> (case-insensitive); unmapped users fall
    /// back to <see cref="DefaultRole"/>.
    /// </summary>
    public IDictionary<string, IDictionary<string, string>> TenantRoleAssignments { get; }
        = new Dictionary<string, IDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default role assigned to authenticated users with no explicit assignment in
    /// <see cref="TenantRoleAssignments"/>. <c>null</c> (the default) means no implicit
    /// role — unmapped users are denied every command. Operators can opt in to a
    /// least-privilege fallback by setting this to <see cref="ViewerRole"/>.
    /// </summary>
    public string? DefaultRole { get; set; }

    /// <summary>
    /// Seed <see cref="RoleCommands"/> with the canonical Operator/Approver/Viewer matrix
    /// from <c>architecture.md</c> §5.2. Existing entries are preserved (so an operator can
    /// add a custom role first and then call this method to fill in the canonical roles).
    /// </summary>
    /// <returns>The same <see cref="RbacOptions"/> instance (fluent).</returns>
    public RbacOptions WithDefaultRoleMatrix()
    {
        if (!RoleCommands.ContainsKey(OperatorRole))
        {
            RoleCommands[OperatorRole] = new ReadOnlyCollection<string>(new List<string>(AllCommands));
        }

        if (!RoleCommands.ContainsKey(ApproverRole))
        {
            RoleCommands[ApproverRole] = new ReadOnlyCollection<string>(new List<string>
            {
                "approve",
                "reject",
                "agent status",
            });
        }

        if (!RoleCommands.ContainsKey(ViewerRole))
        {
            RoleCommands[ViewerRole] = new ReadOnlyCollection<string>(new List<string>
            {
                "agent status",
            });
        }

        return this;
    }

    /// <summary>
    /// Register an AAD object ID's role in a specific tenant. Idempotent — re-registering
    /// the same user replaces the role.
    /// </summary>
    /// <param name="tenantId">The Entra ID tenant the user belongs to.</param>
    /// <param name="aadObjectId">The user's AAD object ID (Entra <c>oid</c> claim).</param>
    /// <param name="role">The role name; must match a key in <see cref="RoleCommands"/>.</param>
    /// <returns>The same <see cref="RbacOptions"/> instance (fluent).</returns>
    public RbacOptions AssignRole(string tenantId, string aadObjectId, string role)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(aadObjectId)) throw new ArgumentException("AAD object ID is required.", nameof(aadObjectId));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("Role is required.", nameof(role));

        if (!TenantRoleAssignments.TryGetValue(tenantId, out var assignments))
        {
            assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TenantRoleAssignments[tenantId] = assignments;
        }

        assignments[aadObjectId] = role;
        return this;
    }

    /// <summary>
    /// Look up the role assignment for the supplied user. Returns
    /// <see cref="DefaultRole"/> when no explicit assignment exists, which may itself be
    /// <c>null</c> (unmapped — deny by default).
    /// </summary>
    public string? ResolveRoleOrDefault(string tenantId, string aadObjectId)
    {
        if (!string.IsNullOrEmpty(tenantId)
            && TenantRoleAssignments.TryGetValue(tenantId, out var assignments)
            && assignments.TryGetValue(aadObjectId, out var role)
            && !string.IsNullOrWhiteSpace(role))
        {
            return role;
        }

        return DefaultRole;
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied role is permitted to execute the supplied
    /// command. Returns <c>false</c> when the role is unknown, the command is not in the
    /// role's permitted set, or either input is null/empty.
    /// </summary>
    public bool IsCommandPermitted(string? role, string command)
    {
        if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(command))
        {
            return false;
        }

        return RoleCommands.TryGetValue(role, out var allowed) && allowed.Contains(command);
    }

    /// <summary>
    /// Return the most-privileged role that permits <paramref name="command"/>. Used to
    /// populate <c>AuthorizationResult.RequiredRole</c> for the access-denied Adaptive Card.
    /// Returns <c>null</c> when no configured role grants the command.
    /// </summary>
    public string? FindRequiredRole(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        // Prefer the canonical role-name order (Viewer first because it is the
        // lowest-privilege role that might suffice for read commands like
        // "agent status"). The explanation card always names the lowest-privilege
        // role required to execute the rejected command.
        foreach (var canonical in new[] { ViewerRole, ApproverRole, OperatorRole })
        {
            if (RoleCommands.TryGetValue(canonical, out var allowed) && allowed.Contains(command))
            {
                return canonical;
            }
        }

        foreach (var kvp in RoleCommands)
        {
            if (kvp.Value.Contains(command))
            {
                return kvp.Key;
            }
        }

        return null;
    }
}
