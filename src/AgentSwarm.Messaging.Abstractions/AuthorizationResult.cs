namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Result of an RBAC check performed by <see cref="IUserAuthorizationService.AuthorizeAsync"/>.
/// Aligned with <c>architecture.md</c> §4.10.
/// </summary>
/// <param name="IsAuthorized"><c>true</c> when the user is permitted to execute the requested command; <c>false</c> otherwise.</param>
/// <param name="UserRole">The role assigned to the user (e.g., <c>Operator</c>, <c>Approver</c>, <c>Viewer</c>). Null when the user is unmapped or has no role assignment.</param>
/// <param name="RequiredRole">The role required to execute the requested command, used to populate the access-denied Adaptive Card explanation when <see cref="IsAuthorized"/> is <c>false</c>. Null on successful authorization.</param>
public sealed record AuthorizationResult(
    bool IsAuthorized,
    string? UserRole,
    string? RequiredRole);
