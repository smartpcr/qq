// -----------------------------------------------------------------------
// <copyright file="SlackMembershipResolutionException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;

/// <summary>
/// Thrown by <see cref="ISlackMembershipResolver"/> implementations when
/// Slack's <c>usergroups.users.list</c> call fails (transient network
/// error, HTTP 5xx, rate-limit exhaustion, malformed bot token, etc.).
/// <see cref="SlackAuthorizationFilter"/> catches this exception and
/// surfaces a controlled
/// <see cref="SlackAuthorizationRejectionReason.MembershipResolutionFailed"/>
/// rejection so the security pipeline "fails closed".
/// </summary>
public sealed class SlackMembershipResolutionException : Exception
{
    /// <summary>
    /// Slack workspace the resolver was probing.
    /// </summary>
    public string TeamId { get; }

    /// <summary>
    /// Slack user-group the resolver was probing when the failure
    /// occurred. <see langword="null"/> when the failure occurred
    /// before a specific group was selected (e.g., the bot token
    /// could not be resolved).
    /// </summary>
    public string? UserGroupId { get; }

    /// <summary>
    /// Creates a new exception describing a membership lookup failure.
    /// </summary>
    public SlackMembershipResolutionException(string teamId, string? userGroupId, string message, Exception? inner = null)
        : base(message, inner)
    {
        this.TeamId = teamId ?? string.Empty;
        this.UserGroupId = userGroupId;
    }
}
