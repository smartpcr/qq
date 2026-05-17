// -----------------------------------------------------------------------
// <copyright file="SlackSocketModeOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;

/// <summary>
/// Knobs for <see cref="SlackSocketModeReceiver"/>: reconnection
/// backoff bounds, envelope ACK deadline, and the receive-buffer size
/// passed to the underlying <see cref="System.Net.WebSockets.ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Defaults match architecture.md §2.2.3 ("Reconnection uses exponential
/// backoff with jitter (initial 1 s, max 30 s)") and tech-spec.md §5.2
/// ("each event envelope must be acknowledged within 5 seconds via the
/// WebSocket connection").
/// </para>
/// </remarks>
public sealed class SlackSocketModeOptions
{
    /// <summary>
    /// Configuration section name bound by
    /// <c>SlackInboundTransportServiceCollectionExtensions.AddSlackSocketModeTransport(IConfiguration)</c>.
    /// Operators override any field of this class from
    /// <c>appsettings.json</c>:
    /// <code><![CDATA[
    /// "Slack": {
    ///   "SocketMode": {
    ///     "InitialReconnectDelay": "00:00:01",
    ///     "MaxReconnectDelay": "00:00:30",
    ///     "AckTimeout": "00:00:04",
    ///     "ReceiveBufferSize": 16384
    ///   }
    /// }
    /// ]]></code>
    /// </summary>
    public const string SectionName = "Slack:SocketMode";

    /// <summary>
    /// Initial reconnect delay applied to the first reconnect attempt
    /// after a disconnection. Subsequent attempts grow the delay
    /// exponentially up to <see cref="MaxReconnectDelay"/>. Default
    /// is one second per architecture.md §2.2.3.
    /// </summary>
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on the reconnect delay. Default is thirty seconds
    /// per architecture.md §2.2.3.
    /// </summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum time the receiver will wait for a single envelope ACK
    /// write to complete on the WebSocket. Slack requires every
    /// envelope to be ACKed within five seconds (tech-spec.md §5.2);
    /// the receiver enforces a slightly tighter timeout so a stuck
    /// socket fails fast and triggers reconnection before the budget
    /// is exhausted.
    /// </summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Receive-buffer size, in bytes, used when assembling text frames
    /// from the underlying WebSocket. Defaults to <c>16 KiB</c> which
    /// fits the vast majority of Slack event envelopes without
    /// fragmentation while keeping the per-connection memory footprint
    /// small.
    /// </summary>
    public int ReceiveBufferSize { get; set; } = 16 * 1024;
}
