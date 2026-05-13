namespace Qq.Messaging.Abstractions;

/// <summary>
/// Maps platform-level identities to authorized operator identities
/// and enforces chat/user allowlists.
/// </summary>
public interface IOperatorRegistry
{
    /// <summary>
    /// Resolve a platform principal to an authorized operator.
    /// Returns null if the principal is not mapped.
    /// </summary>
    Task<OperatorIdentity?> GetOperatorAsync(
        PlatformPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check whether a platform principal is on the allowlist.
    /// </summary>
    Task<bool> IsAuthorizedAsync(
        PlatformPrincipal principal,
        CancellationToken cancellationToken = default);
}
