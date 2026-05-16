// -----------------------------------------------------------------------
// <copyright file="ISlackWorkspaceConfigStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Resolves a <see cref="SlackWorkspaceConfig"/> from a Slack workspace
/// identifier (<c>team_id</c>) for the security pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Introduced by Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// <c>SlackSignatureValidator</c> uses this contract to fetch the signing
/// secret reference before computing the HMAC, and the Stage 3.2
/// authorization filter will reuse it for the workspace-membership ACL.
/// </para>
/// <para>
/// The interface is deliberately narrow so the production implementation
/// (an EF Core-backed lookup against the <c>slack_workspace_config</c>
/// table introduced by Stage 2.2 / 2.3) can be substituted for the
/// in-memory implementation used by tests without forcing either side to
/// know about the other's storage layer.
/// </para>
/// </remarks>
public interface ISlackWorkspaceConfigStore
{
    /// <summary>
    /// Looks up the workspace configuration row keyed by
    /// <paramref name="teamId"/>.
    /// </summary>
    /// <param name="teamId">
    /// Slack <c>team_id</c> (e.g., <c>T0123ABCD</c>). Case-sensitive.
    /// Treated as a missing workspace when <see langword="null"/>, empty,
    /// or whitespace -- callers must handle the <see langword="null"/>
    /// result rather than relying on an exception.
    /// </param>
    /// <param name="ct">Cancellation token honoured by network-backed stores.</param>
    /// <returns>
    /// The matching row when a workspace configuration exists for the
    /// supplied <paramref name="teamId"/> AND it is
    /// <see cref="SlackWorkspaceConfig.Enabled"/>; otherwise
    /// <see langword="null"/>. Implementations MUST filter disabled
    /// rows at this boundary so callers (notably the Stage 3.2
    /// authorization filter) can trust a non-null result is a usable
    /// workspace without re-checking
    /// <see cref="SlackWorkspaceConfig.Enabled"/>. Stage 3.1 iter-4
    /// evaluator item 2 codified this contract after a mismatch was
    /// flagged between the docstring and both shipped stores.
    /// </returns>
    Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct);

    /// <summary>
    /// Enumerates every workspace that is registered AND
    /// <see cref="SlackWorkspaceConfig.Enabled"/>. Used by
    /// <c>SlackSignatureValidator</c> for the Events API
    /// <c>url_verification</c> handshake, which does not carry a
    /// <c>team_id</c> -- the validator must try each registered signing
    /// secret to satisfy the brief's "url_verification" requirement
    /// (architecture.md §2.2.2).
    /// </summary>
    /// <param name="ct">Cancellation token honoured by network-backed stores.</param>
    Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct);
}
