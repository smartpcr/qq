// -----------------------------------------------------------------------
// <copyright file="ISlackMembershipResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Resolves whether a Slack user belongs to at least one of the user
/// groups in an allow-list. Used by <see cref="SlackAuthorizationFilter"/>
/// to enforce the third layer of the three-layer ACL.
/// </summary>
/// <remarks>
/// Implementations are expected to cache results with a TTL so a hot path
/// (every inbound slash command, every interaction) does not call Slack's
/// <c>usergroups.users.list</c> on every request. The cache TTL is
/// configurable via
/// <see cref="AgentSwarm.Messaging.Slack.Configuration.SlackConnectorOptions.MembershipCacheTtlMinutes"/>
/// (default 5 minutes per the implementation-plan brief).
/// </remarks>
public interface ISlackMembershipResolver
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="userId"/>
    /// belongs to at least one of <paramref name="allowedUserGroupIds"/>
    /// inside <paramref name="teamId"/>.
    /// </summary>
    /// <param name="teamId">Slack workspace identifier.</param>
    /// <param name="userId">Slack user identifier of the requester.</param>
    /// <param name="allowedUserGroupIds">
    /// Allow-list of Slack user-group ids. An empty collection always
    /// returns <see langword="false"/> (deny-all per the workspace
    /// configuration contract).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the user is a member of any allowed
    /// group; otherwise <see langword="false"/>. Implementations MUST
    /// throw <see cref="SlackMembershipResolutionException"/> when an
    /// underlying Slack API call fails, so the filter can surface a
    /// controlled <c>MembershipResolutionFailed</c> rejection.
    /// </returns>
    Task<bool> IsUserInAnyAllowedGroupAsync(
        string teamId,
        string userId,
        IReadOnlyCollection<string> allowedUserGroupIds,
        CancellationToken ct);
}
