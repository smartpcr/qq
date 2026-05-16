using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Concrete <see cref="IIdentityResolver"/> that maps an Entra AAD object ID (captured from
/// the inbound <c>Activity.From.AadObjectId</c>) to a platform-agnostic
/// <see cref="UserIdentity"/> via the application-supplied <see cref="IUserDirectory"/>.
/// Replaces the <see cref="DefaultDenyIdentityResolver"/> stub registered in Stage 2.1.
/// Aligned with <c>tech-spec.md</c> §4.2 rejection matrix row 3,
/// <c>architecture.md</c> §4.9 / §5.2, and <c>implementation-plan.md</c> §5.1 step 3.
/// </summary>
/// <remarks>
/// Returning <c>null</c> from <see cref="ResolveAsync"/> indicates the AAD object ID is not
/// mapped in the configured directory; the calling
/// <see cref="TeamsSwarmActivityHandler"/> then issues an HTTP 200 + Adaptive Card
/// explaining the access-denial reason and the <c>UnmappedUserRejected</c> action.
/// </remarks>
public sealed class EntraIdentityResolver : IIdentityResolver
{
    private readonly IUserDirectory _directory;
    private readonly ILogger<EntraIdentityResolver> _logger;

    /// <summary>Construct an <see cref="EntraIdentityResolver"/>.</summary>
    /// <param name="directory">Directory provider that maps AAD object IDs to internal users.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public EntraIdentityResolver(IUserDirectory directory, ILogger<EntraIdentityResolver> logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<UserIdentity?> ResolveAsync(string aadObjectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(aadObjectId))
        {
            _logger.LogDebug("EntraIdentityResolver received an empty AAD object ID; treating as unmapped.");
            return null;
        }

        var identity = await _directory.LookupAsync(aadObjectId, ct).ConfigureAwait(false);
        if (identity is null)
        {
            _logger.LogWarning(
                "EntraIdentityResolver: AAD object ID {AadObjectId} not mapped in the directory; rejecting as UnmappedUserRejected.",
                aadObjectId);
            return null;
        }

        return identity;
    }
}
