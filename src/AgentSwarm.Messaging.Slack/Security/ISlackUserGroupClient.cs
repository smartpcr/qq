// -----------------------------------------------------------------------
// <copyright file="ISlackUserGroupClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Per-workspace facade over Slack's <c>usergroups.users.list</c> Web API
/// call. Abstracted out of <see cref="SlackMembershipResolver"/> so the
/// resolver can be unit-tested without dragging in SlackNet's HTTP
/// transport, and so the production implementation
/// (<see cref="SlackNetUserGroupClient"/>) can resolve the per-workspace
/// bot token through the
/// <see cref="AgentSwarm.Messaging.Core.Secrets.ISecretProvider"/> chain.
/// </summary>
public interface ISlackUserGroupClient
{
    /// <summary>
    /// Returns the user IDs that belong to <paramref name="userGroupId"/>
    /// inside <paramref name="teamId"/>.
    /// </summary>
    /// <param name="teamId">Slack workspace identifier.</param>
    /// <param name="userGroupId">Slack user-group identifier (e.g., <c>S0123</c>).</param>
    /// <param name="ct">Cancellation token honoured by the underlying HTTP call.</param>
    /// <returns>
    /// The list of <c>user_id</c> values. Implementations MUST NOT return
    /// <see langword="null"/>; an empty collection is returned for a
    /// group that exists but has no current members.
    /// </returns>
    Task<IReadOnlyCollection<string>> ListUserGroupMembersAsync(
        string teamId,
        string userGroupId,
        CancellationToken ct);
}
