// -----------------------------------------------------------------------
// <copyright file="SlackNetAuthTester.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System;
using System.Collections.Concurrent;
using System.Linq;
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
/// <see cref="ISlackApiClient"/> per workspace, reused across probes
/// to avoid the per-invocation <c>HttpClient</c> socket-exhaustion
/// failure mode. Workspace bot tokens are long-lived and the set is
/// bounded by <c>SlackConnectorOptions.MaxWorkspaces</c> so an
/// eviction policy is unnecessary.
/// </para>
/// <para>
/// The cache is <b>keyed by <c>team_id</c></b>, not by the bot OAuth
/// token. Slack workspace IDs are non-secret stable identifiers (e.g.,
/// <c>T0123ABCD</c>), so they are safe to retain in a process-lifetime
/// dictionary key set. The bot token is captured only inside the
/// short-lived <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
/// factory lambda on cache miss; once the lambda returns, the token
/// reference is reachable only through the SlackNet client instance
/// itself (which needs it to sign Slack Web API requests). This keeps
/// secret material out of the long-lived dictionary key set and
/// narrows the memory-dump credential-extraction window.
/// </para>
/// <para>
/// The class implements <see cref="IDisposable"/> so that when the
/// hosting DI container disposes its root <see cref="IServiceProvider"/>
/// at process shutdown, every cached SlackNet client (and the
/// <see cref="System.Net.Http.HttpClient"/> it owns internally) is
/// drained and disposed deterministically. This prevents the
/// token-rotation leak described in the Stage 7.3 review where a
/// caller mutated <c>SlackWorkspaceConfig.BotTokenSecretRef</c> at
/// runtime and the previously-cached client lingered until the
/// process exited.
/// </para>
/// <para>
/// All failures (other than cancellation and post-dispose access) are
/// swallowed and surfaced through
/// <see cref="SlackAuthTestResult.IsHealthy"/> = <see langword="false"/>
/// rather than thrown -- the Stage 7.3 health-check contract requires
/// the probe to ALWAYS report a status, even on misconfiguration.
/// </para>
/// </remarks>
internal sealed class SlackNetAuthTester : ISlackAuthTester, IDisposable
{
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly Func<string, ISlackApiClient> apiClientFactory;
    private readonly ILogger<SlackNetAuthTester> logger;
    private readonly ConcurrentDictionary<string, ISlackApiClient> apiClientCache;
    private int disposed;

    /// <summary>
    /// Production constructor. Wires the parameterless SlackNet
    /// <see cref="SlackApiClient"/> constructor as the per-workspace
    /// factory; the per-instance cache (see
    /// <see cref="apiClientCache"/>) keys reuse by <c>team_id</c> so
    /// repeated <c>auth.test</c> probes for the same workspace share
    /// a single client (and its internal
    /// <see cref="System.Net.Http.HttpClient"/>).
    /// </summary>
    public SlackNetAuthTester(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<SlackNetAuthTester> logger)
        : this(workspaceStore, secretProvider, static token => new SlackApiClient(token), logger)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets unit tests inject a fake
    /// SlackNet client factory. The factory delegate is invoked at
    /// most once per <c>team_id</c> -- subsequent probes for the same
    /// workspace are served from <see cref="apiClientCache"/>.
    /// </summary>
    /// <param name="apiClientFactory">
    /// Maps a bot OAuth token to an <see cref="ISlackApiClient"/>.
    /// The factory MUST be safe to call concurrently; the cache uses
    /// <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
    /// which may invoke the factory more than once under a race even
    /// though only one returned value is retained.
    /// </param>
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

        // Keyed by Slack team_id (e.g., "T0123ABCD"), an opaque
        // case-sensitive non-secret identifier. Ordinal comparison
        // matches Slack's case-sensitive ID semantics and avoids any
        // culture-sensitive surprises in cross-locale deployments.
        this.apiClientCache = new ConcurrentDictionary<string, ISlackApiClient>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<SlackAuthTestResult> TestAsync(string teamId, CancellationToken ct)
    {
        // Defensive guard for the (extremely unlikely) case where a
        // readiness probe fires AFTER the DI container has disposed
        // this singleton. The hosting model normally stops the
        // listener before draining IDisposable singletons, so this
        // should never trip in practice -- but throwing here surfaces
        // a host-shutdown ordering bug clearly rather than returning
        // a misleading "Slack is unhealthy" result.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);

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
            // Resolve (or build) the cached SlackNet client for this
            // workspace. The dictionary key is the non-secret team_id;
            // the bot token is captured only by the short-lived
            // factory lambda on a cache miss, so the secret never
            // serves as a long-lived dictionary key. On a cache hit
            // the lambda is not invoked and `botToken` falls out of
            // scope at the end of this method.
            ISlackApiClient apiClient = this.apiClientCache.GetOrAdd(
                teamId,
                _ => this.apiClientFactory(botToken));

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
    /// Drains <see cref="apiClientCache"/> and disposes every cached
    /// SlackNet client that implements <see cref="IDisposable"/>,
    /// releasing the internal <see cref="System.Net.Http.HttpClient"/>
    /// each holds. Invoked by the DI container at host shutdown
    /// because <see cref="SlackNetAuthTester"/> is registered as a
    /// singleton via <c>TryAddSingleton</c> in
    /// <see cref="SlackHealthChecksServiceCollectionExtensions.AddSlackHealthChecks"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent: a double <see cref="Dispose"/> is a no-op courtesy
    /// of the <see cref="Interlocked.Exchange(ref int, int)"/> guard.
    /// Disposal exceptions from individual clients are caught and
    /// logged so one misbehaving client cannot block the others from
    /// being cleaned up.
    /// </para>
    /// <para>
    /// SlackNet's <see cref="SlackApiClient"/> does not always
    /// implement <see cref="IDisposable"/> across versions, so the
    /// cast is performed defensively via <c>as IDisposable</c>: a
    /// non-disposable client is removed from the cache but otherwise
    /// left for the GC to collect.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        // Snapshot the keys before iterating so concurrent additions
        // during shutdown (extremely rare -- the host stops the
        // listener first) cannot corrupt the enumeration. Any client
        // added after this snapshot is taken will simply be skipped
        // here and reclaimed by the GC; it cannot be served to a new
        // probe because the `disposed` flag short-circuits TestAsync.
        foreach (string teamId in this.apiClientCache.Keys.ToArray())
        {
            if (!this.apiClientCache.TryRemove(teamId, out ISlackApiClient? client))
            {
                continue;
            }

            if (client is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(
                        ex,
                        "Slack auth.test: failed to dispose cached SlackNet client for workspace {TeamId} during shutdown.",
                        teamId);
                }
            }
        }
    }
}
