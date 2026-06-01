// -----------------------------------------------------------------------
// <copyright file="SlackNetUserGroupClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using SlackNet;

/// <summary>
/// Production <see cref="ISlackUserGroupClient"/> that resolves the
/// per-workspace bot OAuth token via
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>,
/// builds a per-call <see cref="SlackApiClient"/>, and dispatches the
/// <c>usergroups.users.list</c> call through SlackNet's typed
/// <see cref="SlackNet.WebApi.IUserGroupUsersApi"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The brief calls for the resolver to invoke Slack's
/// <c>usergroups.users.list</c> "via SlackNet"; this class is the only
/// place in the connector that constructs a SlackNet client, keeping the
/// dependency surface narrow.
/// </para>
/// <para>
/// A new <see cref="SlackApiClient"/> is constructed per call because the
/// bot token is workspace-scoped and we have one shared transport
/// abstraction. The cost is dominated by the outbound HTTP request, so
/// the per-call allocation is amortised. <see cref="SlackMembershipResolver"/>
/// further cushions the call rate by caching membership snapshots with a
/// configurable TTL.
/// </para>
/// </remarks>
public sealed class SlackNetUserGroupClient : ISlackUserGroupClient
{
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly Func<string, ISlackApiClient> apiClientFactory;

    /// <summary>
    /// Production constructor: builds a fresh SlackNet
    /// <see cref="SlackApiClient"/> per call using the supplied bot
    /// token.
    /// </summary>
    public SlackNetUserGroupClient(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider)
        : this(workspaceStore, secretProvider, apiClientFactory: token => new SlackApiClient(token))
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets a unit test inject a
    /// SlackNet client factory (e.g., one that returns a fake
    /// implementing the SlackNet API surface).
    /// </summary>
    public SlackNetUserGroupClient(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        Func<string, ISlackApiClient> apiClientFactory)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> ListUserGroupMembersAsync(
        string teamId,
        string userGroupId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            throw new ArgumentException("Team id must be supplied.", nameof(teamId));
        }

        if (string.IsNullOrWhiteSpace(userGroupId))
        {
            throw new ArgumentException("User group id must be supplied.", nameof(userGroupId));
        }

        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(teamId, ct)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            throw new InvalidOperationException(
                $"Slack workspace '{teamId}' is not registered or is disabled; cannot resolve user-group membership.");
        }

        if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
        {
            throw new InvalidOperationException(
                $"Slack workspace '{teamId}' has no bot-token secret reference; cannot call usergroups.users.list.");
        }

        string botToken = await this.secretProvider
            .GetSecretAsync(workspace.BotTokenSecretRef, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(botToken))
        {
            throw new InvalidOperationException(
                $"Slack workspace '{teamId}' bot-token secret resolved to an empty string.");
        }

        ISlackApiClient client = this.apiClientFactory(botToken);
        IReadOnlyList<string> members = await client.UserGroupUsers
            .List(userGroupId, includeDisabled: false, ct)
            .ConfigureAwait(false);

        return members ?? Array.Empty<string>();
    }
}
