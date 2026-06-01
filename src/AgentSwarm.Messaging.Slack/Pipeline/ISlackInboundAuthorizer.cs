// -----------------------------------------------------------------------
// <copyright file="ISlackInboundAuthorizer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Envelope-level Slack authorization gate used by the Stage 4.3
/// inbound ingestor. The companion to <c>SlackAuthorizationFilter</c>
/// from Stage 3.2: it evaluates the SAME three-layer ACL (workspace
/// -&gt; channel -&gt; user group) but against a queued
/// <see cref="SlackInboundEnvelope"/> rather than a live ASP.NET Core
/// <c>HttpContext</c>, so the background ingestor can re-check
/// authorization independently of the HTTP transport layer.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Architecture.md §574-575 mandates that authorization runs BEFORE
/// the idempotency check so an unauthorized request cannot consume an
/// idempotency-table slot (denial-of-service surface) or trigger a
/// duplicate-audit row that hides the real rejection-audit row.
/// </para>
/// <para>
/// Re-running the ACL in the async pipeline is defence-in-depth: even
/// though Stage 3.2's MVC filter already ran for HTTP-mounted
/// transports, a workspace can have its
/// <see cref="Entities.SlackWorkspaceConfig.Enabled"/> flag flipped to
/// <see langword="false"/> (or its allow-list shrunk) between ACK and
/// ingestor processing. Re-checking here keeps the contract honest.
/// </para>
/// </remarks>
internal interface ISlackInboundAuthorizer
{
    /// <summary>
    /// Evaluates the three-layer Slack ACL against the supplied
    /// envelope and writes a
    /// <see cref="Security.SlackAuthorizationAuditRecord"/> through
    /// the registered <see cref="Security.ISlackAuthorizationAuditSink"/>
    /// on every rejection (per Stage 3.2 audit contract).
    /// </summary>
    Task<SlackInboundAuthorizationResult> AuthorizeAsync(SlackInboundEnvelope envelope, CancellationToken ct);
}
