// -----------------------------------------------------------------------
// <copyright file="SlackNetAuthTester.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.WebApi;

/// <summary>
/// Default <see cref="ISlackAuthTester"/> implementation. Resolves the
/// per-workspace bot OAuth token through the
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>
/// chain, dispatches <c>auth.test</c> via SlackNet's
/// <see cref="ISlackApiClient"/>, and converts SlackNet's typed
/// exceptions into a stable <see cref="SlackAuthTestResult"/> shape so
/// the health check never has to think about transport-level types.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Mirrors the caching SlackNet client pattern established by
/// <see cref="Transport.SlackDirectApiClient"/>: one
/// <see cref="ISlackApiClient"/> per bot token, reused across calls
/// to avoid the per-invocation <c>HttpClient</c> socket-exhaustion
/// failure mode. Workspace bot tokens are long-lived and the set is
/// bounded by <c>SlackConnectorOptions.MaxWorkspaces</c> so an
/// eviction policy is unnecessary.
/// </para>
/// <para>
/// All failures are swallowed and surfaced through
/// <see cref="SlackAuthTestResult.IsHealthy"/> = <see langword="false"/>
/// rather than thrown -- the Stage 7.3 health-check contract requires
/// the probe to ALWAYS report a status, even on misconfiguration.
/// </para>
/// </remarks>
internal sealed class SlackNetAuthTester : ISlackAuthTester
{
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly Func<string, ISlackApiClient> apiClientFactory;
    private readonly ILogger<SlackNetAuthTester> logger;

    /// <summary>
    /// Production constructor: closes over a per-instance bot-token
    /// cache so repeated <c>auth.test</c> probes reuse the same
    /// SlackNet <see cref="SlackApiClient"/> (and its internal
    /// <see cref="System.Net.Http.HttpClient"/>).
    /// </summary>
    public SlackNetAuthTester(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<SlackNetAuthTester> logger)
        : this(workspaceStore, secretProvider, BuildCachingApiClientFactory(), logger)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets unit tests inject a
    /// fake SlackNet client factory keyed by bot token.
    /// </summary>
    internal SlackNetAuthTester(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        Func<string, ISlackApiClient> apiClientFactory,
        ILogger<SlackNetAuthTester> logger)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SlackAuthTestResult> TestAsync(string teamId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return new SlackAuthTestResult(
                TeamId: teamId ?? string.Empty,
                IsHealthy: false,
                Detail: "team_id is null or empty; cannot resolve workspace bot token.");
        }

        SlackWorkspaceConfig? workspace;
        try
        {
            workspace = await this.workspaceStore
                .GetByTeamIdAsync(teamId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack auth.test health probe: failed to resolve workspace {TeamId} from ISlackWorkspaceConfigStore.",
                teamId);
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: false,
                Detail: $"failed to resolve workspace from store: {ex.GetType().Name}");
        }

        if (workspace is null)
        {
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: false,
                Detail: $"workspace '{teamId}' is not registered or is disabled.");
        }

        if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
        {
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: false,
                Detail: $"workspace '{teamId}' has no bot-token secret reference.");
        }

        string? botToken;
        try
        {
            botToken = await this.secretProvider
                .GetSecretAsync(workspace.BotTokenSecretRef, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack auth.test health probe: failed to resolve bot-token secret '{SecretRef}' for workspace {TeamId}.",
                workspace.BotTokenSecretRef,
                teamId);
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: false,
                Detail: $"failed to resolve bot-token secret for workspace '{teamId}'.");
        }

        if (string.IsNullOrEmpty(botToken))
        {
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: false,
                Detail: $"workspace '{teamId}' bot-token secret resolved to empty.");
        }

        try
        {
            ISlackApiClient apiClient = this.apiClientFactory(botToken);
            AuthTestResponse response = await apiClient.Auth.Test(ct).ConfigureAwait(false);
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: true,
                Detail: $"auth.test ok (team={response.Team ?? "?"}, user={response.User ?? "?"})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SlackException sex)
        {
            string errorCode = string.IsNullOrEmpty(sex.ErrorCode) ? "unknown_error" : sex.ErrorCode;
            this.logger.LogWarning(
                sex,
                "Slack auth.test returned {ErrorCode} for workspace {TeamId}.",
                errorCode,
                teamId);
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: false,
                Detail: $"slack returned error '{errorCode}'.",
                ErrorCode: errorCode);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack auth.test transport error for workspace {TeamId}.",
                teamId);
            return new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: false,
                Detail: $"transport error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the production <see cref="ISlackApiClient"/> factory
    /// used by the parameterless constructor. The returned delegate
    /// closes over a private <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// keyed by bot OAuth token so each token reuses a single
    /// <see cref="SlackApiClient"/> instance.
    /// </summary>
    private static Func<string, ISlackApiClient> BuildCachingApiClientFactory()
    {
        ConcurrentDictionary<string, ISlackApiClient> cache = new(StringComparer.Ordinal);
        return token => cache.GetOrAdd(token, static t => new SlackApiClient(t));
    }
}
