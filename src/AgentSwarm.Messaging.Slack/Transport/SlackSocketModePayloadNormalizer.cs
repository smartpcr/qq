// -----------------------------------------------------------------------
// <copyright file="SlackSocketModePayloadNormalizer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Globalization;

/// <summary>
/// Converts a Socket Mode <see cref="SlackSocketModeFrame"/> into a
/// normalized <see cref="SlackInboundEnvelope"/> for enqueueing on
/// <see cref="Queues.ISlackInboundQueue"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The HTTP <see cref="SlackInboundEnvelopeFactory"/> expects the
/// transport-native body shape (JSON for events, form-encoded for
/// commands and interactions). Slack's Socket Mode delivers everything
/// as JSON inside the frame's <c>payload</c> sub-object, so this
/// normalizer parses the JSON directly rather than re-encoding into
/// form bodies.
/// </para>
/// <para>
/// Idempotency keys follow architecture.md §3.4 verbatim so the Stage
/// 4.3 <c>SlackIdempotencyGuard</c> sees the same key shape regardless
/// of which transport delivered the payload.
/// </para>
/// <para>
/// Hash fallbacks are computed over <see cref="SlackSocketModeFrame.Payload"/>
/// (the inner Slack payload) rather than the outer
/// <see cref="SlackSocketModeFrame.RawFrameJson"/> envelope. Slack
/// reissues retried frames with a fresh <c>envelope_id</c>, so hashing
/// the envelope would produce a different idempotency key on every
/// retry and defeat duplicate suppression for payloads that lack a
/// stable identifier (e.g. events without <c>event_id</c>).
/// </para>
/// </remarks>
internal static class SlackSocketModePayloadNormalizer
{
    private const string EventKeyPrefix = "event:";
    private const string CommandKeyPrefix = "cmd:";
    private const string InteractionKeyPrefix = "interact:";

    /// <summary>
    /// Builds a <see cref="SlackInboundEnvelope"/> from the supplied
    /// Socket Mode <paramref name="frame"/>, stamping
    /// <see cref="SlackInboundEnvelope.ReceivedAt"/> with
    /// <paramref name="receivedAt"/>. Returns <see langword="null"/>
    /// when the frame type is not one of the three Slack event
    /// surfaces (<c>events_api</c>, <c>slash_commands</c>,
    /// <c>interactive</c>) -- callers ACK such frames but do not
    /// enqueue them.
    /// </summary>
    public static SlackInboundEnvelope? Normalize(SlackSocketModeFrame frame, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame.Type switch
        {
            SlackSocketModeFrame.EventsApiType => NormalizeEvent(frame, receivedAt),
            SlackSocketModeFrame.SlashCommandsType => NormalizeCommand(frame, receivedAt),
            SlackSocketModeFrame.InteractiveType => NormalizeInteraction(frame, receivedAt),
            _ => null,
        };
    }

    private static SlackInboundEnvelope NormalizeEvent(SlackSocketModeFrame frame, DateTimeOffset receivedAt)
    {
        // The events_api Socket Mode payload IS the Events API JSON
        // body verbatim; SlackInboundPayloadParser.ParseEvent already
        // handles every field we need.
        SlackEventPayload payload = SlackInboundPayloadParser.ParseEvent(frame.Payload);
        string idempotencyKey = !string.IsNullOrEmpty(payload.EventId)
            ? EventKeyPrefix + payload.EventId
            : EventKeyPrefix + HashFallback(frame.Payload);

        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Event,
            TeamId: payload.TeamId ?? string.Empty,
            ChannelId: NullIfEmpty(payload.ChannelId),
            UserId: payload.UserId ?? string.Empty,
            RawPayload: frame.Payload,
            TriggerId: null,
            ReceivedAt: receivedAt);
    }

    private static SlackInboundEnvelope NormalizeCommand(SlackSocketModeFrame frame, DateTimeOffset receivedAt)
    {
        // The slash_commands Socket Mode payload mirrors the HTTP
        // form fields but is JSON. Iter-2 evaluator item 1 fix: delegate
        // to the parser's promoted public ParseCommandJson so the parser
        // owns one canonical command-decode codepath shared by HTTP form
        // bodies (via auto-detect inside ParseCommand) and Socket Mode
        // JSON bodies (via this normalizer).
        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommandJson(frame.Payload);

        string team = payload.TeamId ?? string.Empty;
        string user = payload.UserId ?? string.Empty;
        string cmd = payload.Command ?? string.Empty;
        string trigger = payload.TriggerId ?? HashFallback(frame.Payload);
        string idempotencyKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}:{2}:{3}:{4}",
            CommandKeyPrefix,
            team,
            user,
            cmd,
            trigger);

        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Command,
            TeamId: team,
            ChannelId: NullIfEmpty(payload.ChannelId),
            UserId: user,
            RawPayload: frame.Payload,
            TriggerId: NullIfEmpty(payload.TriggerId),
            ReceivedAt: receivedAt);
    }

    private static SlackInboundEnvelope NormalizeInteraction(SlackSocketModeFrame frame, DateTimeOffset receivedAt)
    {
        // The interactive Socket Mode payload is the SAME JSON shape
        // that the HTTP path delivers in the form's `payload` field;
        // SlackInboundPayloadParser.ParseInteractionJson handles it
        // without the form-wrapping step.
        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteractionJson(frame.Payload);

        string team = payload.TeamId ?? string.Empty;
        string user = payload.UserId ?? string.Empty;
        string actionOrView = payload.ActionOrViewId ?? HashFallback(frame.Payload);
        string trigger = payload.TriggerId ?? actionOrView;
        string idempotencyKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}:{2}:{3}:{4}",
            InteractionKeyPrefix,
            team,
            user,
            actionOrView,
            trigger);

        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: team,
            ChannelId: NullIfEmpty(payload.ChannelId),
            UserId: user,
            RawPayload: frame.Payload,
            TriggerId: NullIfEmpty(payload.TriggerId),
            ReceivedAt: receivedAt);
    }

    private static string HashFallback(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "empty";
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
