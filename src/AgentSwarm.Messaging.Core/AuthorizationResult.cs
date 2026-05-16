using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Outcome of an <see cref="IUserAuthorizationService.AuthorizeAsync"/> call.
/// Carries the allow/deny verdict, a human-readable reason for denials, and
/// (on allow) the resolved <see cref="GuildBinding"/> so downstream pipeline
/// stages can route on the binding's <see cref="GuildBinding.ChannelPurpose"/>,
/// <see cref="GuildBinding.TenantId"/>, and <see cref="GuildBinding.WorkspaceId"/>
/// without re-querying the registry. See architecture.md Section 4.5.
/// </summary>
/// <param name="IsAllowed">
/// <see langword="true"/> when the caller passed every check (binding active,
/// channel permitted, role allowed); otherwise <see langword="false"/>.
/// </param>
/// <param name="DenialReason">
/// Operator-facing reason for the denial. Required when <see cref="IsAllowed"/>
/// is <see langword="false"/>; <see langword="null"/> when allowed. Surfaced via
/// the connector's ephemeral error message.
/// </param>
/// <param name="ResolvedBinding">
/// The <see cref="GuildBinding"/> that authorised the call. Populated when
/// <see cref="IsAllowed"/> is <see langword="true"/>; <see langword="null"/>
/// when denied (the resolution may have failed before a binding was identified).
/// </param>
public sealed record AuthorizationResult(
    bool IsAllowed,
    string? DenialReason,
    GuildBinding? ResolvedBinding)
{
    /// <summary>
    /// Convenience factory for a successful authorisation against a resolved
    /// <see cref="GuildBinding"/>.
    /// </summary>
    public static AuthorizationResult Allow(GuildBinding binding)
    {
        if (binding is null)
        {
            throw new ArgumentNullException(nameof(binding));
        }

        return new AuthorizationResult(IsAllowed: true, DenialReason: null, ResolvedBinding: binding);
    }

    /// <summary>
    /// Convenience factory for a denial with a reason. The optional
    /// <paramref name="binding"/> may be set when the denial happened after the
    /// binding was resolved (e.g. role check failed) so callers can still log
    /// which binding the denial applied to.
    /// </summary>
    public static AuthorizationResult Deny(string reason, GuildBinding? binding = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "reason must not be null, empty, or whitespace for a denial result.",
                nameof(reason));
        }

        return new AuthorizationResult(IsAllowed: false, DenialReason: reason, ResolvedBinding: binding);
    }
}
