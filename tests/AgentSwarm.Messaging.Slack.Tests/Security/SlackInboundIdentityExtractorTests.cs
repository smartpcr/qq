// -----------------------------------------------------------------------
// <copyright file="SlackInboundIdentityExtractorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Xunit;

/// <summary>
/// Pinned parsing behaviour for
/// <see cref="SlackInboundIdentityExtractor"/>. The filter relies on
/// these mappings to identify the requesting team, channel, and user
/// across the three Slack inbound surfaces (events JSON, slash command
/// form, interaction payload-form).
/// </summary>
public sealed class SlackInboundIdentityExtractorTests
{
    [Fact]
    public void Slash_command_form_extracts_team_channel_user_and_combined_command_text()
    {
        // Slack slash command payload, application/x-www-form-urlencoded.
        string body = "team_id=T0123ABCD&channel_id=C9999ALPHA&user_id=U7777BETA&command=%2Fagent&text=ask+write+a+plan";

        SlackInboundIdentity identity = SlackInboundIdentityExtractor.Parse(body, "application/x-www-form-urlencoded");

        identity.TeamId.Should().Be("T0123ABCD");
        identity.ChannelId.Should().Be("C9999ALPHA");
        identity.UserId.Should().Be("U7777BETA");
        identity.CommandText.Should().Be("/agent ask write a plan");
    }

    [Fact]
    public void Event_callback_json_extracts_team_and_nested_channel_user()
    {
        string body = @"{""team_id"":""T0123ABCD"",""event"":{""type"":""app_mention"",""channel"":""C9999ALPHA"",""user"":""U7777BETA""}}";

        SlackInboundIdentity identity = SlackInboundIdentityExtractor.Parse(body, "application/json");

        identity.TeamId.Should().Be("T0123ABCD");
        identity.ChannelId.Should().Be("C9999ALPHA");
        identity.UserId.Should().Be("U7777BETA");
        identity.CommandText.Should().BeNull("event callbacks do not carry slash-command text");
    }

    [Fact]
    public void Interaction_payload_form_field_is_decoded_and_parsed_as_json()
    {
        // Block Kit interactions arrive as form-urlencoded with the
        // JSON payload nested in a `payload` field.
        string payloadJson = @"{""team"":{""id"":""T0123ABCD""},""channel"":{""id"":""C9999ALPHA""},""user"":{""id"":""U7777BETA""},""actions"":[{""value"":""approve""}]}";
        string body = "payload=" + System.Uri.EscapeDataString(payloadJson);

        SlackInboundIdentity identity = SlackInboundIdentityExtractor.Parse(body, "application/x-www-form-urlencoded");

        identity.TeamId.Should().Be("T0123ABCD");
        identity.ChannelId.Should().Be("C9999ALPHA");
        identity.UserId.Should().Be("U7777BETA");
    }

    [Fact]
    public void Empty_body_returns_empty_identity()
    {
        SlackInboundIdentity identity = SlackInboundIdentityExtractor.Parse(string.Empty, "application/json");

        identity.Should().Be(SlackInboundIdentity.Empty);
        identity.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Malformed_json_returns_empty_identity_without_throwing()
    {
        SlackInboundIdentity identity = SlackInboundIdentityExtractor.Parse(
            "{ not valid json",
            "application/json");

        identity.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Json_with_nested_team_object_extracts_team_id_from_team_dot_id()
    {
        string body = @"{""team"":{""id"":""T0123ABCD"",""domain"":""example""}}";

        SlackInboundIdentity identity = SlackInboundIdentityExtractor.Parse(body, "application/json");

        identity.TeamId.Should().Be("T0123ABCD",
            "team.id must be honoured when the top-level team_id property is absent (event callback envelope)");
    }
}
