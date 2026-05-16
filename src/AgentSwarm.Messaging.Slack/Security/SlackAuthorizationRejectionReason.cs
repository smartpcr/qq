// -----------------------------------------------------------------------
// <copyright file="SlackAuthorizationRejectionReason.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

/// <summary>
/// Enumerated reason for a rejection by <see cref="SlackAuthorizationFilter"/>.
/// The filter ALWAYS returns HTTP 200 on rejection (Slack requires it -- see
/// architecture.md §2.4); the discriminator exists so the audit sink and
/// structured logger can distinguish "user not in any allowed group" from
/// "unknown workspace" without parsing free-form text.
/// </summary>
/// <remarks>
/// Mapped onto the brief's <c>outcome = rejected_auth</c> audit value: every
/// entry written through <see cref="ISlackAuthorizationAuditSink"/> stores
/// this enum together with the outcome string so an operator triaging
/// authorization failures can quickly bucket them. Mirrors the Stage 3.1
/// <see cref="SlackSignatureRejectionReason"/> pattern so the audit table
/// has a consistent triage shape across the security pipeline.
/// </remarks>
public enum SlackAuthorizationRejectionReason
{
    /// <summary>
    /// Default value; should never be persisted. Indicates the filter
    /// ran to completion without classifying the request.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The request body did not include a <c>team_id</c> (or it was
    /// empty / whitespace), so the workspace layer of the ACL cannot
    /// be evaluated. Distinct from <see cref="UnknownWorkspace"/> --
    /// that reason means a <c>team_id</c> WAS supplied but is not
    /// registered with the connector.
    /// </summary>
    MissingTeamId = 1,

    /// <summary>
    /// The supplied <c>team_id</c> is not registered with the connector,
    /// or the matching <see cref="AgentSwarm.Messaging.Slack.Entities.SlackWorkspaceConfig"/>
    /// has <see cref="AgentSwarm.Messaging.Slack.Entities.SlackWorkspaceConfig.Enabled"/>
    /// set to <see langword="false"/>.
    /// </summary>
    UnknownWorkspace = 2,

    /// <summary>
    /// The supplied <c>channel_id</c> is missing or not in the
    /// workspace's
    /// <see cref="AgentSwarm.Messaging.Slack.Entities.SlackWorkspaceConfig.AllowedChannelIds"/>
    /// list.
    /// </summary>
    DisallowedChannel = 3,

    /// <summary>
    /// The requesting user does not belong to any user group in the
    /// workspace's
    /// <see cref="AgentSwarm.Messaging.Slack.Entities.SlackWorkspaceConfig.AllowedUserGroupIds"/>
    /// list. Also used when the workspace's allow-list is empty
    /// (deny-all per the entity docstring) or when <c>user_id</c> is
    /// missing from the inbound payload.
    /// </summary>
    UserNotInAllowedGroup = 4,

    /// <summary>
    /// Membership resolution against Slack's <c>usergroups.users.list</c>
    /// API failed transiently (network error, HTTP 5xx, rate-limit
    /// exhaustion). The filter treats this as a controlled rejection
    /// rather than allowing the request to proceed -- "fail closed" is
    /// the correct posture for the security pipeline.
    /// </summary>
    MembershipResolutionFailed = 5,
}
