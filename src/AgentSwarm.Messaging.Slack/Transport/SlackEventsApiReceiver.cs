// -----------------------------------------------------------------------
// <copyright file="SlackEventsApiReceiver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// No-op <see cref="ISlackInboundTransport"/> for workspaces configured to
/// use the Events API HTTP transport.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The HTTP receive path is owned by the Stage 4.1 controllers
/// (<see cref="SlackEventsController"/>,
/// <see cref="SlackCommandsController"/>,
/// <see cref="SlackInteractionsController"/>) which the
/// <c>WebApplication</c> host maps directly. This receiver therefore
/// only exists so the transport factory can return a uniform
/// <see cref="ISlackInboundTransport"/> regardless of which transport a
/// workspace runs -- a deployment that mixes Socket Mode and Events API
/// workspaces sees the same start/stop lifecycle for every entry.
/// </para>
/// <para>
/// The class logs once on start / stop so operators can confirm transport
/// selection (architecture.md §4.2: "the active transport is selected by
/// configuration").
/// </para>
/// </remarks>
internal sealed class SlackEventsApiReceiver : ISlackInboundTransport
{
    private readonly string teamId;
    private readonly ILogger<SlackEventsApiReceiver> logger;

    public SlackEventsApiReceiver(string teamId, ILogger<SlackEventsApiReceiver> logger)
    {
        this.teamId = teamId ?? string.Empty;
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        this.logger?.LogInformation(
            "Slack inbound transport for workspace {TeamId} uses Events API (no Socket Mode app-level token configured). HTTP controllers handle inbound payloads.",
            this.teamId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        this.logger?.LogInformation(
            "Slack inbound transport for workspace {TeamId} (Events API) stopped.",
            this.teamId);
        return Task.CompletedTask;
    }
}
