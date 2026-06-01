// -----------------------------------------------------------------------
// <copyright file="SlackInboundTransportKind.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Discriminator returned by
/// <see cref="ISlackInboundTransportFactory.ResolveTransportKind(AgentSwarm.Messaging.Slack.Entities.SlackWorkspaceConfig)"/>
/// describing which <see cref="ISlackInboundTransport"/> implementation
/// a particular Slack workspace should use.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (and architecture.md §4.2 / tech-spec.md §2.1):
/// </para>
/// <list type="bullet">
///   <item><description><see cref="EventsApi"/> is used when
///   <c>SlackWorkspaceConfig.AppLevelTokenRef</c> is <c>null</c>,
///   empty, or whitespace -- the HTTP <c>/api/slack/*</c> controllers
///   own the receive loop.</description></item>
///   <item><description><see cref="SocketMode"/> is used when
///   <c>SlackWorkspaceConfig.AppLevelTokenRef</c> resolves to a
///   non-empty secret reference -- the connector opens a persistent
///   WebSocket to Slack via the Socket Mode protocol.</description></item>
/// </list>
/// </remarks>
public enum SlackInboundTransportKind
{
    /// <summary>
    /// HTTP Events API transport. The Stage 4.1 ASP.NET controllers
    /// receive the payloads; the matching <see cref="ISlackInboundTransport"/>
    /// implementation is <see cref="SlackEventsApiReceiver"/>, which is a
    /// no-op marker because the host's ASP.NET pipeline owns the work.
    /// </summary>
    EventsApi = 0,

    /// <summary>
    /// Socket Mode WebSocket transport. The connector opens one
    /// WebSocket per workspace via Slack's <c>apps.connections.open</c>
    /// endpoint and reads event envelopes off the socket. Implemented
    /// by <see cref="SlackSocketModeReceiver"/>.
    /// </summary>
    SocketMode = 1,
}
