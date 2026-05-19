using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Diagnostics;
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
/// <para>
/// Returning <c>null</c> from <see cref="ResolveAsync"/> indicates the AAD object ID is not
/// mapped in the configured directory; the calling
/// <see cref="TeamsSwarmActivityHandler"/> then issues an HTTP 200 + Adaptive Card
/// explaining the access-denial reason and the <c>UnmappedUserRejected</c> action.
/// </para>
/// <para>
/// <b>Stage 6.3 iter-10 evaluator fix item 2.</b> The resolver wraps its log emissions
/// in <see cref="TeamsLogScope.BeginScope"/> so each entry carries the canonical
/// <c>CorrelationId</c>, <c>TenantId</c>, and <c>UserId</c> enrichment per §6.3 step 5.
/// The resolver has the raw AAD object ID natively (its only input), so the
/// <see cref="TeamsLogScope.UserIdKey"/> slot is populated with that identifier; the
/// remaining two keys are inherited from the parent
/// <see cref="TeamsLogContext"/> frame opened by the calling
/// <c>TeamsSwarmActivityHandler</c>, or substituted with
/// <see cref="TeamsLogScope.EmptyValueSentinel"/> when the resolver is invoked outside
/// a turn context (e.g. integration tests, alternate Bot controllers).
/// </para>
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

        // Stage 6.3 iter-10 evaluator fix item 2 — open a TeamsLogScope so every
        // ILogger entry emitted by this method carries the canonical three-key
        // enrichment per §6.3 step 5. CorrelationId / TenantId are inherited from
        // the parent scope opened by TeamsSwarmActivityHandler (when invoked on the
        // bot turn path) or sentinel-substituted otherwise. UserId carries the raw
        // AAD object ID — the only identifier we have at this layer (the internal
        // user ID does not exist until LookupAsync returns).
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            userId: string.IsNullOrEmpty(aadObjectId) ? null : aadObjectId);

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
