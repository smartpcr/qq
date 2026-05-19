// -----------------------------------------------------------------------
// <copyright file="SlackStartupDiagnosticsHostedService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 7.3 startup diagnostic logger. On host start, enumerates every
/// active <see cref="SlackWorkspaceConfig"/>, classifies its transport
/// (Events API vs Socket Mode) through the optional
/// <see cref="ISlackInboundTransportFactory.ResolveTransportKind"/>
/// the Stage 4.2 transport hosted service uses, and writes one
/// structured log line per workspace plus a single rate-limit
/// configuration summary line.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 5: "Add diagnostic logging on connector startup: log active
/// workspaces, transport type per workspace (Events API vs Socket
/// Mode), and rate-limit configuration." Brief test scenario:
/// "Given the connector starts with 2 configured workspaces, When
/// startup completes, Then structured logs include workspace IDs
/// and transport types."
/// </para>
/// <para>
/// The <see cref="ISlackInboundTransportFactory"/> dependency is
/// resolved through <see cref="IServiceProvider"/> as an OPTIONAL
/// dependency. The Stage 4.1 Events-API-only host wires
/// <c>AddSlackInboundTransport()</c> -- which does NOT register the
/// transport factory; only <c>AddSlackSocketModeTransport()</c> does.
/// Hosts that mount the health pipeline but skip Socket Mode (Events
/// API + commands only) MUST still boot, so this hosted service
/// degrades gracefully to a "transport classification unavailable"
/// log line per workspace rather than throwing on
/// <c>GetRequiredService</c>.
/// </para>
/// <para>
/// The hosted service is intentionally cheap: enumerate workspaces,
/// resolve transport kinds (when the factory is registered), and log
/// -- nothing more. It does NOT open WebSocket connections (that is
/// owned by <c>SlackInboundTransportHostedService</c>) and it does
/// NOT call any Slack Web API method. The point is to produce a
/// single post-startup audit trail of what the connector intends to
/// do, keyed by team id, so an operator can grep for
/// "Slack startup diagnostics" and immediately know which workspaces
/// are loaded and which transports they use.
/// </para>
/// <para>
/// Failures inside the diagnostics path NEVER block host startup:
/// the workspace-store call is wrapped in a try/catch that logs the
/// failure and returns. The hosted service runs ONCE at start; on
/// stop it is a no-op.
/// </para>
/// </remarks>
internal sealed class SlackStartupDiagnosticsHostedService : IHostedService
{
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly IServiceProvider serviceProvider;
    private readonly IOptions<SlackConnectorOptions> connectorOptions;
    private readonly ILogger<SlackStartupDiagnosticsHostedService> logger;

    public SlackStartupDiagnosticsHostedService(
        ISlackWorkspaceConfigStore workspaceStore,
        IServiceProvider serviceProvider,
        IOptions<SlackConnectorOptions> connectorOptions,
        ILogger<SlackStartupDiagnosticsHostedService> logger)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        this.connectorOptions = connectorOptions ?? throw new ArgumentNullException(nameof(connectorOptions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.WriteWorkspaceDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        this.WriteRateLimitDiagnostics();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Internal test seam: same shape the host's
    /// <see cref="StartAsync"/> invokes but exposed for unit tests
    /// so the sequence is asserted without standing up a full
    /// <see cref="IHost"/>.
    /// </summary>
    internal async Task WriteDiagnosticsAsync(CancellationToken ct)
    {
        await this.WriteWorkspaceDiagnosticsAsync(ct).ConfigureAwait(false);
        this.WriteRateLimitDiagnostics();
    }

    private async Task WriteWorkspaceDiagnosticsAsync(CancellationToken ct)
    {
        IReadOnlyCollection<SlackWorkspaceConfig> workspaces;
        try
        {
            workspaces = await this.workspaceStore
                .GetAllEnabledAsync(ct)
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
                "Slack startup diagnostics: failed to enumerate workspaces; skipping per-workspace transport log lines.");
            return;
        }

        if (workspaces.Count == 0)
        {
            this.logger.LogInformation(
                "Slack startup diagnostics: no enabled workspaces are registered. The connector will refuse inbound traffic until at least one workspace is configured.");
            return;
        }

        this.logger.LogInformation(
            "Slack startup diagnostics: {WorkspaceCount} enabled workspace(s) registered; per-workspace transport selections follow.",
            workspaces.Count);

        // Stage 7.3 evaluator iter-2 item 2: resolve the transport
        // factory OPTIONALLY -- the canonical Worker host wires only
        // AddSlackInboundTransport (Events API) and does NOT register
        // ISlackInboundTransportFactory; only AddSlackSocketModeTransport
        // does. The brief's requirement "log active workspaces,
        // transport type per workspace (Events API vs Socket Mode)" is
        // a function of workspace CONFIGURATION (presence of an
        // app-level token), not of which DI extension is wired. So
        // when the factory is unavailable we fall back to the same
        // one-line inference SlackInboundTransportFactory.ResolveTransportKind
        // performs: a non-empty AppLevelTokenRef => Socket Mode, else
        // Events API. This guarantees the brief-required transport
        // line is always emitted, regardless of which inbound
        // extension the host has wired.
        ISlackInboundTransportFactory? transportFactory =
            this.serviceProvider.GetService<ISlackInboundTransportFactory>();

        foreach (SlackWorkspaceConfig workspace in workspaces)
        {
            SlackInboundTransportKind kind;
            string classificationSource;
            if (transportFactory is not null)
            {
                try
                {
                    kind = transportFactory.ResolveTransportKind(workspace);
                    classificationSource = "factory";
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(
                        ex,
                        "Slack startup diagnostics: ISlackInboundTransportFactory.ResolveTransportKind threw for workspace {TeamId}; falling back to AppLevelTokenRef inference.",
                        workspace.TeamId);
                    kind = InferTransportKindFromConfig(workspace);
                    classificationSource = "config-fallback-after-factory-throw";
                }
            }
            else
            {
                kind = InferTransportKindFromConfig(workspace);
                classificationSource = "config-inferred";
            }

            // The story explicitly calls for "Events API vs Socket
            // Mode" so emit a human-readable display string alongside
            // the strongly-typed enum value. The structured fields
            // (TeamId, TransportKind, TransportKindDisplay) are how
            // operators query logs; the message template carries the
            // same data for greppability.
            string display = kind switch
            {
                SlackInboundTransportKind.EventsApi => "Events API",
                SlackInboundTransportKind.SocketMode => "Socket Mode",
                _ => $"Unknown ({kind})",
            };

            this.logger.LogInformation(
                "Slack startup diagnostics: workspace TeamId={TeamId} WorkspaceName={WorkspaceName} TransportKind={TransportKind} TransportKindDisplay={TransportKindDisplay} TransportClassificationSource={TransportClassificationSource} DefaultChannelId={DefaultChannelId} AllowedChannelCount={AllowedChannelCount} AllowedUserGroupCount={AllowedUserGroupCount}.",
                workspace.TeamId,
                workspace.WorkspaceName,
                kind.ToString(),
                display,
                classificationSource,
                workspace.DefaultChannelId,
                workspace.AllowedChannelIds?.Length ?? 0,
                workspace.AllowedUserGroupIds?.Length ?? 0);
        }
    }

    /// <summary>
    /// Mirrors <see cref="SlackInboundTransportFactory.ResolveTransportKind(SlackWorkspaceConfig)"/>
    /// so the startup diagnostics path can classify a workspace
    /// without requiring the factory to be registered. A non-empty
    /// <see cref="SlackWorkspaceConfig.AppLevelTokenRef"/> indicates
    /// a Socket Mode workspace (the WebSocket transport needs an
    /// app-level token); an empty / null reference indicates an
    /// Events API workspace (HTTPS callbacks signed by the workspace
    /// signing secret only).
    /// </summary>
    internal static SlackInboundTransportKind InferTransportKindFromConfig(SlackWorkspaceConfig workspace)
        => string.IsNullOrWhiteSpace(workspace.AppLevelTokenRef)
            ? SlackInboundTransportKind.EventsApi
            : SlackInboundTransportKind.SocketMode;

    private void WriteRateLimitDiagnostics()
    {
        SlackConnectorOptions options = this.connectorOptions.Value;
        SlackRateLimitOptions rates = options.RateLimits;

        this.logger.LogInformation(
            "Slack startup diagnostics: rate-limit configuration MaxWorkspaces={MaxWorkspaces} MembershipCacheTtlMinutes={MembershipCacheTtlMinutes} Tier1RequestsPerMinute={Tier1RequestsPerMinute} Tier1Scope={Tier1Scope} Tier2RequestsPerMinute={Tier2RequestsPerMinute} Tier2Scope={Tier2Scope} Tier3RequestsPerMinute={Tier3RequestsPerMinute} Tier3Scope={Tier3Scope} Tier4RequestsPerMinute={Tier4RequestsPerMinute} Tier4Scope={Tier4Scope}.",
            options.MaxWorkspaces,
            options.MembershipCacheTtlMinutes,
            rates.Tier1.RequestsPerMinute,
            rates.Tier1.Scope,
            rates.Tier2.RequestsPerMinute,
            rates.Tier2.Scope,
            rates.Tier3.RequestsPerMinute,
            rates.Tier3.Scope,
            rates.Tier4.RequestsPerMinute,
            rates.Tier4.Scope);
    }
}
