// -----------------------------------------------------------------------
// <copyright file="SlackInboundAuthorizationResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;

/// <summary>
/// Outcome of <see cref="ISlackInboundAuthorizer.AuthorizeAsync"/>.
/// Mirrors the three-layer ACL surface defined by Stage 3.2 (workspace
/// -&gt; channel -&gt; user group) but evaluated against a queued
/// <see cref="Transport.SlackInboundEnvelope"/> instead of a live
/// <c>HttpContext</c> so the Stage 4.3 ingestor can run the same
/// authorization gate from a background service.
/// </summary>
/// <param name="IsAuthorized">
/// <see langword="true"/> when every layer of the ACL passed and the
/// envelope may proceed to idempotency / dispatch;
/// <see langword="false"/> when any layer rejected the envelope. The
/// ingestor MUST drop unauthorized envelopes BEFORE acquiring an
/// idempotency record so a malicious caller cannot poison the dedup
/// table with bogus keys (architecture.md §§574-575).
/// </param>
/// <param name="Workspace">
/// Resolved <see cref="SlackWorkspaceConfig"/> when
/// <paramref name="IsAuthorized"/> is <see langword="true"/>;
/// <see langword="null"/> otherwise.
/// </param>
/// <param name="Reason">
/// Discriminator for the layer that rejected the envelope. Set to
/// <see cref="SlackAuthorizationRejectionReason.Unspecified"/> on the
/// happy path.
/// </param>
/// <param name="Detail">
/// Free-form diagnostic text describing the rejection (e.g.
/// <c>"team_id 'T0' not registered"</c>). <see langword="null"/> on
/// the happy path.
/// </param>
internal readonly record struct SlackInboundAuthorizationResult(
    bool IsAuthorized,
    SlackWorkspaceConfig? Workspace,
    SlackAuthorizationRejectionReason Reason,
    string? Detail)
{
    /// <summary>
    /// Convenience constructor for an authorized result.
    /// </summary>
    public static SlackInboundAuthorizationResult Authorized(SlackWorkspaceConfig workspace)
        => new(true, workspace, SlackAuthorizationRejectionReason.Unspecified, null);

    /// <summary>
    /// Convenience constructor for a rejection.
    /// </summary>
    public static SlackInboundAuthorizationResult Rejected(SlackAuthorizationRejectionReason reason, string detail)
        => new(false, null, reason, detail);
}
