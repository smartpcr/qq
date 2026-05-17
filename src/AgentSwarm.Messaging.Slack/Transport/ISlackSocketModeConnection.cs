// -----------------------------------------------------------------------
// <copyright file="ISlackSocketModeConnection.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Test-friendly seam around the underlying Slack Socket Mode
/// WebSocket. Production wires
/// <see cref="ClientWebSocketSlackSocketModeConnection"/>; unit tests
/// inject a fake that scripts the desired sequence of frames.
/// </summary>
/// <remarks>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The brief's "mock WebSocket server" test scenario is realised here
/// rather than by spinning a real loopback HTTP listener so the test
/// suite stays in-process and avoids port conflicts on CI.
/// </remarks>
internal interface ISlackSocketModeConnection : IAsyncDisposable
{
    /// <summary>
    /// Reads the next text frame from the socket. Returns
    /// <see langword="null"/> when the peer closes the connection
    /// gracefully so the receive loop can react by reconnecting via
    /// the backoff policy.
    /// </summary>
    Task<SlackSocketModeFrame?> ReceiveFrameAsync(CancellationToken ct);

    /// <summary>
    /// Sends the Socket Mode ACK frame
    /// (<c>{"envelope_id":"&lt;id&gt;"}</c>) for the supplied
    /// envelope. Slack requires the ACK to arrive within five
    /// seconds of the corresponding event frame.
    /// </summary>
    Task SendAckAsync(string envelopeId, CancellationToken ct);

    /// <summary>
    /// Closes the underlying WebSocket cleanly. Idempotent: a second
    /// call on an already-closed connection is a no-op.
    /// </summary>
    Task CloseAsync(CancellationToken ct);
}
