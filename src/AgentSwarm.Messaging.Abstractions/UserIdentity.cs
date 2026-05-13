namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Internal user identity returned by <see cref="IIdentityResolver.ResolveAsync"/>. Aligned
/// with <c>architecture.md</c> §4.9.
/// </summary>
/// <param name="InternalUserId">Platform-agnostic internal user identifier — used by the orchestrator when addressing proactive questions via <see cref="AgentQuestion.TargetUserId"/>.</param>
/// <param name="AadObjectId">Entra ID AAD object ID for the user — the native Teams identity captured from <c>Activity.From.AadObjectId</c>.</param>
/// <param name="DisplayName">Human-readable display name (for rendering in cards and audit logs).</param>
/// <param name="Role">RBAC role assigned to the user (for example, <c>Operator</c>, <c>Approver</c>, or <c>Viewer</c>). Role-to-command mappings are defined in <c>architecture.md</c> §5.2.</param>
public sealed record UserIdentity(
    string InternalUserId,
    string AadObjectId,
    string DisplayName,
    string Role);
