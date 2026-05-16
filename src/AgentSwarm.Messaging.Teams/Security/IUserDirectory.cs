using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Directory contract used by <see cref="EntraIdentityResolver"/> to look up an internal
/// user record by Entra AAD object ID. Operators plug in an LDAP, Microsoft Graph, SCIM, or
/// a static implementation (the bundled <see cref="StaticUserDirectory"/> is suitable for
/// small deployments and tests).
/// </summary>
/// <remarks>
/// Returning <c>null</c> indicates the user is not mapped — the activity handler then
/// rejects the inbound request with an Adaptive Card explaining access denial (the
/// two-tier rejection model defined in <c>tech-spec.md</c> §4.2 row 3).
/// </remarks>
public interface IUserDirectory
{
    /// <summary>
    /// Map an Entra AAD object ID to the platform-agnostic <see cref="UserIdentity"/>.
    /// </summary>
    /// <param name="aadObjectId">The user's AAD object ID (Entra <c>oid</c> claim).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mapped identity, or <c>null</c> when no row exists.</returns>
    Task<UserIdentity?> LookupAsync(string aadObjectId, CancellationToken cancellationToken);
}
