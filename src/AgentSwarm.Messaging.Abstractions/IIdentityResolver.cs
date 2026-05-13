namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Maps a messenger-native external user identifier (for Teams, the
/// <c>Activity.From.AadObjectId</c>) to the platform-agnostic internal user identity record.
/// Aligned with <c>architecture.md</c> §4.9.
/// </summary>
/// <remarks>
/// Returning <c>null</c> from <see cref="ResolveAsync"/> indicates the user is not mapped and
/// triggers the unmapped-user rejection flow described in <c>architecture.md</c> §6.4.2.
/// </remarks>
public interface IIdentityResolver
{
    /// <summary>
    /// Resolve the supplied AAD object ID to an internal <see cref="UserIdentity"/>.
    /// </summary>
    /// <param name="aadObjectId">The Entra ID AAD object ID of the inbound activity sender.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>
    /// The resolved <see cref="UserIdentity"/>, or <c>null</c> if the user is not mapped in
    /// the configured directory.
    /// </returns>
    Task<UserIdentity?> ResolveAsync(string aadObjectId, CancellationToken ct);
}
