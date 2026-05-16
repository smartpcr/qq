// -----------------------------------------------------------------------
// <copyright file="SlackSocketModePayloadNormalizerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

/// <summary>
/// Pins the Stage 4.2 normalization of Socket Mode frames into
/// canonical <see cref="SlackInboundEnvelope"/> instances. Frame types
/// other than <c>events_api</c>, <c>slash_commands</c>, and
/// <c>interactive</c> return <see langword="null"/> so callers ACK
/// without enqueueing.
/// </summary>
public sealed class SlackSocketModePayloadNormalizerTests
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Normalize_returns_null_for_hello_frame()
    {
        SlackSocketModeFrame hello = new(
            Type: SlackSocketModeFrame.HelloType,
            EnvelopeId: null,
            Payload: string.Empty,
            RawFrameJson: "{\"type\":\"hello\"}");

        SlackInboundEnvelope? envelope = SlackSocketModePayloadNormalizer.Normalize(hello, ReceivedAt);

        envelope.Should().BeNull();
    }

    [Fact]
    public void Normalize_events_api_frame_uses_event_id_key()
    {
        const string payload = """
            {
              "type": "event_callback",
              "event_id": "Ev123",
              "team_id": "T01TEAM",
              "event": { "type": "app_mention", "user": "U01USER", "channel": "C01CHAN" }
            }
            """;
        SlackSocketModeFrame frame = new(
            Type: SlackSocketModeFrame.EventsApiType,
            EnvelopeId: "env-abc",
            Payload: payload,
            RawFrameJson: "{\"envelope_id\":\"env-abc\",\"type\":\"events_api\",\"payload\":" + payload + "}");

        SlackInboundEnvelope envelope = SlackSocketModePayloadNormalizer
            .Normalize(frame, ReceivedAt)!;

        envelope.Should().NotBeNull();
        envelope.SourceType.Should().Be(SlackInboundSourceType.Event);
        envelope.IdempotencyKey.Should().Be("event:Ev123",
            "architecture.md §3.4 mandates event:{event_id}");
        envelope.TeamId.Should().Be("T01TEAM");
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.UserId.Should().Be("U01USER");
        envelope.TriggerId.Should().BeNull();
        envelope.RawPayload.Should().Be(payload);
        envelope.ReceivedAt.Should().Be(ReceivedAt);
    }

    [Fact]
    public void Normalize_slash_commands_frame_uses_trigger_keyed_idempotency()
    {
        const string payload = """
            {
              "team_id": "T01TEAM",
              "channel_id": "C01CHAN",
              "user_id": "U01USER",
              "command": "/agent",
              "text": "ask hello",
              "trigger_id": "trigger-42"
            }
            """;
        SlackSocketModeFrame frame = new(
            Type: SlackSocketModeFrame.SlashCommandsType,
            EnvelopeId: "env-cmd",
            Payload: payload,
            RawFrameJson: "{\"envelope_id\":\"env-cmd\",\"type\":\"slash_commands\",\"payload\":" + payload + "}");

        SlackInboundEnvelope envelope = SlackSocketModePayloadNormalizer
            .Normalize(frame, ReceivedAt)!;

        envelope.SourceType.Should().Be(SlackInboundSourceType.Command);
        envelope.IdempotencyKey.Should().Be("cmd:T01TEAM:U01USER:/agent:trigger-42");
        envelope.TeamId.Should().Be("T01TEAM");
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.UserId.Should().Be("U01USER");
        envelope.TriggerId.Should().Be("trigger-42");
        envelope.RawPayload.Should().Be(payload);
    }

    [Fact]
    public void Normalize_interactive_frame_uses_action_keyed_idempotency()
    {
        const string payload = """
            {
              "type": "block_actions",
              "team": { "id": "T01TEAM" },
              "channel": { "id": "C01CHAN" },
              "user": { "id": "U01USER" },
              "trigger_id": "trigger-99",
              "actions": [ { "action_id": "approve" } ]
            }
            """;
        SlackSocketModeFrame frame = new(
            Type: SlackSocketModeFrame.InteractiveType,
            EnvelopeId: "env-int",
            Payload: payload,
            RawFrameJson: "{\"envelope_id\":\"env-int\",\"type\":\"interactive\",\"payload\":" + payload + "}");

        SlackInboundEnvelope envelope = SlackSocketModePayloadNormalizer
            .Normalize(frame, ReceivedAt)!;

        envelope.SourceType.Should().Be(SlackInboundSourceType.Interaction);
        envelope.IdempotencyKey.Should().Be("interact:T01TEAM:U01USER:approve:trigger-99");
        envelope.TeamId.Should().Be("T01TEAM");
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.UserId.Should().Be("U01USER");
        envelope.TriggerId.Should().Be("trigger-99");
        envelope.RawPayload.Should().Be(payload);
    }

    [Fact]
    public void Normalize_returns_null_for_disconnect_frame()
    {
        SlackSocketModeFrame disc = new(
            Type: SlackSocketModeFrame.DisconnectType,
            EnvelopeId: null,
            Payload: string.Empty,
            RawFrameJson: "{\"type\":\"disconnect\",\"reason\":\"refresh_requested\"}");

        SlackInboundEnvelope? envelope = SlackSocketModePayloadNormalizer.Normalize(disc, ReceivedAt);

        envelope.Should().BeNull();
    }

    [Fact]
    public void Normalize_falls_back_to_hashed_idempotency_when_event_id_missing()
    {
        const string payload = """
            { "type": "event_callback", "team_id": "T01TEAM",
              "event": { "type": "app_mention", "user": "U01USER", "channel": "C01CHAN" } }
            """;
        SlackSocketModeFrame frame = new(
            Type: SlackSocketModeFrame.EventsApiType,
            EnvelopeId: "env-x",
            Payload: payload,
            RawFrameJson: "{\"envelope_id\":\"env-x\",\"type\":\"events_api\",\"payload\":" + payload + "}");

        SlackInboundEnvelope envelope = SlackSocketModePayloadNormalizer
            .Normalize(frame, ReceivedAt)!;

        envelope.IdempotencyKey.Should().StartWith("event:");
        envelope.IdempotencyKey.Length.Should().BeGreaterThan("event:".Length,
            "the SHA-256 hex fallback should populate the key when event_id is missing");
    }
}
