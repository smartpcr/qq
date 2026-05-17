// -----------------------------------------------------------------------
// <copyright file="ISlackSocketModeConnectionFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Creates a fresh <see cref="ISlackSocketModeConnection"/> bound to a
/// Slack workspace's app-level token. The factory encapsulates the
/// two-step Socket Mode handshake (1: call
/// <c>https://slack.com/api/apps.connections.open</c> to retrieve a
/// WSS URL; 2: open the WebSocket against that URL) so the receiver
/// can stay focused on the receive loop.
/// </summary>
/// <remarks>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The factory is the seam unit tests substitute to simulate Slack
/// (rather than mocking <see cref="System.Net.WebSockets.ClientWebSocket"/>
/// directly which is sealed).
/// </remarks>
internal interface ISlackSocketModeConnectionFactory
{
    /// <summary>
    /// Opens a new Socket Mode connection for the workspace whose
    /// <c>SlackWorkspaceConfig.AppLevelTokenRef</c> resolves to
    /// <paramref name="appLevelToken"/>.
    /// </summary>
    /// <param name="appLevelToken">
    /// Resolved Slack app-level token (the <c>xapp-...</c> string
    /// returned by <see cref="AgentSwarm.Messaging.Core.Secrets.ISecretProvider.GetSecretAsync"/>).
    /// Never null or whitespace; the caller validates that.
    /// </param>
    /// <param name="ct">Cancellation honoured during the
    /// HTTP-then-WebSocket handshake.</param>
    Task<ISlackSocketModeConnection> ConnectAsync(string appLevelToken, CancellationToken ct);
}
