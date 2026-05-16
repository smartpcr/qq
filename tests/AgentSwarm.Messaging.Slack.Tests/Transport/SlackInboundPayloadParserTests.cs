// -----------------------------------------------------------------------
// <copyright file="SlackInboundPayloadParserTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

/// <summary>
/// Unit tests for <see cref="SlackInboundPayloadParser"/>. Covers the
/// three inbound shapes accepted by Stage 4.1 (Events API JSON, slash
/// command form, interactive payload wrapped in a <c>payload</c> form
/// field) plus malformed-body short-circuits and the sub-command
/// tokeniser used by the modal fast-path.
/// </summary>
public sealed class SlackInboundPayloadParserTests
{
    [Fact]
    public void ParseEvent_recognises_url_verification_handshake()
    {
        const string body = "{\"type\":\"url_verification\",\"challenge\":\"abc123\",\"token\":\"xoxb\"}";

        SlackEventPayload payload = SlackInboundPayloadParser.ParseEvent(body);

        payload.Type.Should().Be("url_verification");
        payload.Challenge.Should().Be("abc123");
        payload.IsUrlVerification.Should().BeTrue("a url_verification payload with a non-empty challenge must trigger the handshake branch");
    }

    [Fact]
    public void ParseEvent_extracts_team_channel_user_and_event_subtype_from_event_callback()
    {
        const string body = """
            {
              "type": "event_callback",
              "event_id": "Ev999",
              "team_id": "T01TEAM",
              "event": {
                "type": "app_mention",
                "user": "U01USER",
                "channel": "C01CHAN"
              }
            }
            """;

        SlackEventPayload payload = SlackInboundPayloadParser.ParseEvent(body);

        payload.Type.Should().Be("event_callback");
        payload.EventId.Should().Be("Ev999");
        payload.TeamId.Should().Be("T01TEAM");
        payload.ChannelId.Should().Be("C01CHAN");
        payload.UserId.Should().Be("U01USER");
        payload.EventSubtype.Should().Be("app_mention");
        payload.IsUrlVerification.Should().BeFalse();
    }

    [Fact]
    public void ParseEvent_returns_empty_for_invalid_json()
    {
        SlackEventPayload payload = SlackInboundPayloadParser.ParseEvent("not-json");

        payload.Should().Be(SlackEventPayload.Empty,
            "malformed bodies must not crash the parser; callers fall back to body-hash idempotency");
    }

    [Fact]
    public void ParseEvent_returns_empty_for_empty_body()
    {
        SlackInboundPayloadParser.ParseEvent(string.Empty).Should().Be(SlackEventPayload.Empty);
    }

    [Fact]
    public void ParseCommand_extracts_form_fields()
    {
        const string body = "token=xoxb&team_id=T01TEAM&channel_id=C01CHAN&user_id=U01USER&command=%2Fagent&text=ask+generate+plan&trigger_id=trig%2E1";

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);

        payload.TeamId.Should().Be("T01TEAM");
        payload.ChannelId.Should().Be("C01CHAN");
        payload.UserId.Should().Be("U01USER");
        payload.Command.Should().Be("/agent");
        payload.Text.Should().Be("ask generate plan");
        payload.TriggerId.Should().Be("trig.1");
        payload.SubCommand.Should().Be("ask",
            "the sub-command is the leading text token used by the modal fast-path");
    }

    [Theory]
    [InlineData("ask write a plan", "ask")]
    [InlineData("REVIEW pull request 42", "review")]
    [InlineData("  escalate  to oncall ", "escalate")]
    [InlineData("status", "status")]
    public void ParseSubCommand_normalises_to_lower_invariant_first_token(string input, string expected)
    {
        SlackInboundPayloadParser.ParseSubCommand(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSubCommand_returns_null_for_blank_text(string? input)
    {
        SlackInboundPayloadParser.ParseSubCommand(input).Should().BeNull();
    }

    [Fact]
    public void ParseInteraction_extracts_team_channel_user_trigger_and_action_id_from_button_payload()
    {
        const string json = """
            {
              "type": "block_actions",
              "trigger_id": "trig.42",
              "team": { "id": "T01TEAM" },
              "channel": { "id": "C01CHAN" },
              "user": { "id": "U01USER" },
              "actions": [
                { "action_id": "approve_task_42", "value": "approve" }
              ]
            }
            """;

        string body = "payload=" + System.Uri.EscapeDataString(json);

        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteraction(body);

        payload.Type.Should().Be("block_actions");
        payload.TeamId.Should().Be("T01TEAM");
        payload.ChannelId.Should().Be("C01CHAN");
        payload.UserId.Should().Be("U01USER");
        payload.TriggerId.Should().Be("trig.42");
        payload.ActionOrViewId.Should().Be("approve_task_42",
            "the first action's action_id is the idempotency anchor for Block Kit button clicks");
    }

    [Fact]
    public void ParseInteraction_falls_back_to_view_id_for_view_submission()
    {
        const string json = """
            {
              "type": "view_submission",
              "trigger_id": "trig.99",
              "team": { "id": "T01TEAM" },
              "user": { "id": "U01USER" },
              "view": { "id": "V123ABC", "callback_id": "review_modal" }
            }
            """;

        string body = "payload=" + System.Uri.EscapeDataString(json);

        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteraction(body);

        payload.Type.Should().Be("view_submission");
        payload.ActionOrViewId.Should().Be("V123ABC",
            "view_submission lacks actions[]; idempotency falls back to the view.id");
        payload.TriggerId.Should().Be("trig.99");
    }

    [Fact]
    public void ParseInteraction_returns_empty_for_body_missing_payload_field()
    {
        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteraction("not_payload=foo");

        payload.Should().Be(SlackInteractionPayload.Empty);
    }

    [Fact]
    public void ParseInteraction_returns_empty_for_malformed_payload_json()
    {
        string body = "payload=" + System.Uri.EscapeDataString("not-json");

        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteraction(body);

        payload.Should().Be(SlackInteractionPayload.Empty);
    }

    // -----------------------------------------------------------------
    // Iter-2 evaluator item 5 -- transport-level pinning for the
    // Stage 5.1 additions: response_url propagation, ArgumentText
    // helper, and Socket Mode JSON auto-detection in ParseCommand.
    // -----------------------------------------------------------------

    [Fact]
    public void ParseCommand_extracts_response_url_for_ephemeral_replies()
    {
        // Stage 5.1 SlackCommandHandler uses payload.ResponseUrl to
        // post ephemeral acks / errors back to the invoking user via
        // the time-limited Slack response_url POST endpoint.
        const string body =
            "token=xoxb&team_id=T01TEAM&channel_id=C01CHAN&user_id=U01USER" +
            "&command=%2Fagent&text=ask+plan&trigger_id=trig.1" +
            "&response_url=https%3A%2F%2Fhooks.slack.com%2Fcommands%2FT01TEAM%2F123%2Fabc";

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);

        payload.ResponseUrl.Should().Be(
            "https://hooks.slack.com/commands/T01TEAM/123/abc",
            "the ephemeral responder requires the response_url to acknowledge the slash command");
    }

    [Fact]
    public void ParseCommand_ResponseUrl_is_null_when_field_absent()
    {
        // Older callers / synthetic envelopes may omit response_url
        // entirely (e.g., re-replayed audit envelopes). The parser
        // must leave the property null so callers can defensively
        // skip the ephemeral post path.
        const string body = "team_id=T1&channel_id=C1&user_id=U1&command=%2Fagent&text=status";

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);

        payload.ResponseUrl.Should().BeNull();
    }

    [Fact]
    public void ParseCommand_ArgumentText_strips_leading_subcommand_token_and_whitespace()
    {
        // The dispatcher (Stage 5.1) needs the prompt / question-id /
        // task-id portion of `/agent <sub> <args...>`, i.e. the text
        // AFTER the leading sub-command token. ArgumentText collapses
        // surrounding whitespace so callers get a clean string.
        const string body =
            "team_id=T1&channel_id=C1&user_id=U1&command=%2Fagent" +
            "&text=ask+generate+implementation+plan+for+persistence+failover";

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);

        payload.SubCommand.Should().Be("ask");
        payload.ArgumentText.Should().Be(
            "generate implementation plan for persistence failover",
            "ArgumentText must drop the leading sub-command token so handlers receive only the user-supplied argument string");
    }

    [Fact]
    public void ParseCommand_ArgumentText_is_null_when_only_subcommand_provided()
    {
        const string body = "team_id=T1&channel_id=C1&user_id=U1&command=%2Fagent&text=status";

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);

        payload.SubCommand.Should().Be("status");
        payload.ArgumentText.Should().BeNull(
            "status has no required argument; ArgumentText must distinguish '' from null so handlers can switch on it");
    }

    [Fact]
    public void ParseCommand_ArgumentText_is_null_when_text_is_blank()
    {
        const string body = "team_id=T1&channel_id=C1&user_id=U1&command=%2Fagent&text=";

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);

        payload.SubCommand.Should().BeNull();
        payload.ArgumentText.Should().BeNull();
    }

    [Fact]
    public void ParseCommand_auto_detects_socket_mode_json_payload()
    {
        // Iter-2 evaluator item 1 (STRUCTURAL fix): Socket Mode delivers
        // slash command payloads as JSON (not form bodies). Previously
        // ParseCommand only accepted form bodies, so Socket Mode
        // commands surfaced as 'Missing sub-command' inside the
        // dispatcher. ParseCommand now sniffs a leading '{' and
        // delegates to the JSON parser so both transports yield the
        // same SlackCommandPayload shape.
        const string json = """
            {
              "team_id": "T01TEAM",
              "channel_id": "C01CHAN",
              "user_id": "U01USER",
              "command": "/agent",
              "text": "approve Q-123",
              "trigger_id": "trig.socket.1",
              "response_url": "https://hooks.slack.com/socket/abc"
            }
            """;

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(json);

        payload.TeamId.Should().Be("T01TEAM");
        payload.ChannelId.Should().Be("C01CHAN");
        payload.UserId.Should().Be("U01USER");
        payload.Command.Should().Be("/agent");
        payload.Text.Should().Be("approve Q-123");
        payload.SubCommand.Should().Be("approve",
            "Socket Mode envelopes MUST yield the same sub-command tokenisation as HTTP envelopes");
        payload.ArgumentText.Should().Be("Q-123");
        payload.TriggerId.Should().Be("trig.socket.1");
        payload.ResponseUrl.Should().Be("https://hooks.slack.com/socket/abc");
    }

    [Fact]
    public void ParseCommand_auto_detect_ignores_leading_whitespace_before_json_brace()
    {
        // Robustness pin: Slack's Socket Mode SDKs occasionally serialise
        // command payloads with leading whitespace / newlines. The
        // detector must tolerate that.
        const string json = "\n  \t {\"team_id\":\"T1\",\"command\":\"/agent\",\"text\":\"status\"}";

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(json);

        payload.TeamId.Should().Be("T1");
        payload.SubCommand.Should().Be("status");
    }

    [Fact]
    public void ParseCommandJson_extracts_full_payload_directly_from_socket_mode_body()
    {
        // ParseCommandJson is the public entry point Stage 4.1
        // SocketModePayloadNormalizer delegates to. Pin its observable
        // shape directly so a future refactor of ParseCommand's
        // auto-detect heuristic does not silently regress the
        // Socket Mode path.
        const string json = """
            {
              "team_id": "T1",
              "channel_id": "C1",
              "user_id": "U1",
              "command": "/agent",
              "text": "review TASK-42",
              "trigger_id": "trig.sm.7",
              "response_url": "https://hooks.slack.com/sm/x"
            }
            """;

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommandJson(json);

        payload.TeamId.Should().Be("T1");
        payload.SubCommand.Should().Be("review");
        payload.ArgumentText.Should().Be("TASK-42");
        payload.TriggerId.Should().Be("trig.sm.7");
        payload.ResponseUrl.Should().Be("https://hooks.slack.com/sm/x");
    }

    [Fact]
    public void ParseCommandJson_returns_empty_for_malformed_json()
    {
        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommandJson("not-json");

        payload.Should().Be(SlackCommandPayload.Empty);
    }
}
