// -----------------------------------------------------------------------
// <copyright file="SlackSocketModeFrame.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Decoded Socket Mode WebSocket text frame as defined by the Slack
/// Socket Mode protocol
/// (<see href="https://api.slack.com/apis/socket-mode"/>).
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Every Slack-delivered frame is a JSON object with at minimum a
/// <c>type</c> field. The five types the connector handles are:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="HelloType"/>: published once per
///   successful WebSocket handshake. The receiver logs it and resets
///   its reconnect counter.</description></item>
///   <item><description><see cref="DisconnectType"/>: Slack is asking
///   the client to reconnect (rolling deploy, token refresh, etc.).
///   The receiver tears down the current socket and reconnects via
///   the backoff policy.</description></item>
///   <item><description><see cref="EventsApiType"/>,
///   <see cref="SlashCommandsType"/>, <see cref="InteractiveType"/>:
///   the three Slack event surfaces the connector consumes. Each
///   carries an <c>envelope_id</c> the receiver MUST echo back on
///   the socket within Slack's five-second ACK deadline.</description></item>
/// </list>
/// <para>
/// <see cref="Payload"/> is the raw JSON of the <c>payload</c>
/// sub-object (re-serialized to a string so the downstream
/// <see cref="SlackInboundEnvelopeFactory"/> can ingest it the same
/// way the HTTP transport ingests its body). For the
/// <see cref="DisconnectType"/> / <see cref="HelloType"/> frames the
/// payload is the full raw frame because Slack does not nest a
/// <c>payload</c> object on those types.
/// </para>
/// </remarks>
internal sealed record SlackSocketModeFrame(
    string Type,
    string? EnvelopeId,
    string Payload,
    string RawFrameJson)
{
    /// <summary>
    /// Slack Socket Mode frame type <c>"hello"</c>: emitted once per
    /// successful WebSocket handshake.
    /// </summary>
    public const string HelloType = "hello";

    /// <summary>
    /// Slack Socket Mode frame type <c>"disconnect"</c>: Slack is
    /// asking the client to close the socket and reconnect.
    /// </summary>
    public const string DisconnectType = "disconnect";

    /// <summary>
    /// Slack Socket Mode frame type <c>"events_api"</c>: wraps an
    /// Events API callback in a Socket Mode envelope.
    /// </summary>
    public const string EventsApiType = "events_api";

    /// <summary>
    /// Slack Socket Mode frame type <c>"slash_commands"</c>: wraps a
    /// slash-command invocation.
    /// </summary>
    public const string SlashCommandsType = "slash_commands";

    /// <summary>
    /// Slack Socket Mode frame type <c>"interactive"</c>: wraps a
    /// Block Kit / view-submission interactive payload.
    /// </summary>
    public const string InteractiveType = "interactive";
}
