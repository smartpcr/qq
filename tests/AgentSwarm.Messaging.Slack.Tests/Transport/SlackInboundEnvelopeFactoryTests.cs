// -----------------------------------------------------------------------
// <copyright file="SlackInboundEnvelopeFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Security.Cryptography;
using System.Text;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

/// <summary>
/// Unit tests for <see cref="SlackInboundEnvelopeFactory"/>. Pins the
/// idempotency-key derivations from architecture.md §3.4 and the
/// SHA-256 hash fallback used when Slack omits the natural key
/// component.
/// </summary>
public sealed class SlackInboundEnvelopeFactoryTests
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_event_derives_idempotency_key_from_event_id()
    {
        const string body = """
            {
              "type": "event_callback",
              "event_id": "Ev123456",
              "team_id": "T01TEAM",
              "event": {
                "type": "app_mention",
                "user": "U01USER",
                "channel": "C01CHAN"
              }
            }
            """;

        SlackInboundEnvelope envelope = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Event, body, ReceivedAt);

        envelope.IdempotencyKey.Should().Be("event:Ev123456",
            "architecture.md §3.4 mandates the event:{event_id} prefix");
        envelope.SourceType.Should().Be(SlackInboundSourceType.Event);
        envelope.TeamId.Should().Be("T01TEAM");
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.UserId.Should().Be("U01USER");
        envelope.TriggerId.Should().BeNull("Events API callbacks never carry a trigger_id");
        envelope.RawPayload.Should().Be(body);
        envelope.ReceivedAt.Should().Be(ReceivedAt);
    }

    [Fact]
    public void Build_event_falls_back_to_body_hash_when_event_id_missing()
    {
        const string body = "{\"type\":\"event_callback\",\"team_id\":\"T01TEAM\"}";

        SlackInboundEnvelope envelope = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Event, body, ReceivedAt);

        string expectedHash = ComputeSha256Hex(body);
        envelope.IdempotencyKey.Should().Be("event:" + expectedHash,
            "missing event_id must not produce 'event:' alone; the body hash provides a stable retry-safe fallback");
    }

    [Fact]
    public void Build_command_concatenates_team_user_command_trigger()
    {
        const string body = "team_id=T01TEAM&channel_id=C01CHAN&user_id=U01USER&command=%2Fagent&text=ask+plan&trigger_id=trig.X";

        SlackInboundEnvelope envelope = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Command, body, ReceivedAt);

        envelope.IdempotencyKey.Should().Be("cmd:T01TEAM:U01USER:/agent:trig.X",
            "architecture.md §3.4 keys slash commands by team:user:command:trigger_id");
        envelope.SourceType.Should().Be(SlackInboundSourceType.Command);
        envelope.TeamId.Should().Be("T01TEAM");
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.UserId.Should().Be("U01USER");
        envelope.TriggerId.Should().Be("trig.X");
        envelope.RawPayload.Should().Be(body);
    }

    [Fact]
    public void Build_command_falls_back_to_body_hash_when_trigger_id_missing()
    {
        const string body = "team_id=T01TEAM&user_id=U01USER&command=%2Fagent&text=ask+plan";

        SlackInboundEnvelope envelope = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Command, body, ReceivedAt);

        string expectedHash = ComputeSha256Hex(body);
        envelope.IdempotencyKey.Should().Be("cmd:T01TEAM:U01USER:/agent:" + expectedHash);
        envelope.TriggerId.Should().BeNull();
    }

    [Fact]
    public void Build_interaction_concatenates_team_user_action_trigger()
    {
        const string json = """
            {
              "type": "block_actions",
              "trigger_id": "trig.42",
              "team": { "id": "T01TEAM" },
              "channel": { "id": "C01CHAN" },
              "user": { "id": "U01USER" },
              "actions": [ { "action_id": "approve_task_42" } ]
            }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);

        SlackInboundEnvelope envelope = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Interaction, body, ReceivedAt);

        envelope.IdempotencyKey.Should().Be("interact:T01TEAM:U01USER:approve_task_42:trig.42",
            "architecture.md §3.4 keys interactions by team:user:action_or_view_id:trigger_id");
        envelope.SourceType.Should().Be(SlackInboundSourceType.Interaction);
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.TriggerId.Should().Be("trig.42");
    }

    [Fact]
    public void Build_interaction_for_view_submission_uses_view_id()
    {
        const string json = """
            {
              "type": "view_submission",
              "trigger_id": "trig.99",
              "team": { "id": "T01TEAM" },
              "user": { "id": "U01USER" },
              "view": { "id": "V123ABC" }
            }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);

        SlackInboundEnvelope envelope = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Interaction, body, ReceivedAt);

        envelope.IdempotencyKey.Should().Be("interact:T01TEAM:U01USER:V123ABC:trig.99");
        envelope.ChannelId.Should().BeNull("view_submission payloads are not channel-scoped");
    }

    [Fact]
    public void Build_throws_for_unspecified_source_type()
    {
        Action act = () => SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Unspecified, "{}", ReceivedAt);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("sourceType");
    }

    [Fact]
    public void Same_event_body_replayed_produces_same_idempotency_key()
    {
        // Slack's at-least-once delivery contract: a retried payload is
        // byte-identical, so the derived key must be stable across the
        // two calls so the downstream idempotency guard recognises the
        // duplicate.
        const string body = """
            {
              "type": "event_callback",
              "event_id": "Ev777",
              "team_id": "T01TEAM"
            }
            """;

        SlackInboundEnvelope first = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Event, body, ReceivedAt);
        SlackInboundEnvelope second = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Event, body, ReceivedAt.AddSeconds(30));

        first.IdempotencyKey.Should().Be(second.IdempotencyKey,
            "the idempotency key must be a pure function of the payload, not the receive timestamp");
    }

    private static string ComputeSha256Hex(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
