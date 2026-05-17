// -----------------------------------------------------------------------
// <copyright file="ISlackInboundTransportFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Builds the <see cref="ISlackInboundTransport"/> implementation
/// appropriate for the supplied workspace based on
/// <see cref="SlackWorkspaceConfig.AppLevelTokenRef"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (and architecture.md §4.2): "The active transport is selected by
/// configuration (<c>SlackWorkspaceConfig.app_level_token_ref</c>
/// present = Socket Mode; absent = Events API)."
/// </para>
/// <para>
/// The factory exposes both <see cref="ResolveTransportKind"/> (pure
/// classification, callable without side effects -- ideal for
/// diagnostics + tests) and <see cref="Create"/> (which builds the
/// concrete transport).
/// </para>
/// </remarks>
internal interface ISlackInboundTransportFactory
{
    /// <summary>
    /// Classifies the transport <paramref name="workspaceConfig"/>
    /// should use without instantiating one. Returns
    /// <see cref="SlackInboundTransportKind.SocketMode"/> when the
    /// app-level token reference is set; otherwise
    /// <see cref="SlackInboundTransportKind.EventsApi"/>.
    /// </summary>
    SlackInboundTransportKind ResolveTransportKind(SlackWorkspaceConfig workspaceConfig);

    /// <summary>
    /// Instantiates the <see cref="ISlackInboundTransport"/> for the
    /// supplied <paramref name="workspaceConfig"/>. The caller owns
    /// the returned instance and is responsible for
    /// <see cref="ISlackInboundTransport.StartAsync"/> /
    /// <see cref="ISlackInboundTransport.StopAsync"/> lifecycle.
    /// </summary>
    ISlackInboundTransport Create(SlackWorkspaceConfig workspaceConfig);
}
