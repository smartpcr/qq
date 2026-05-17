// -----------------------------------------------------------------------
// <copyright file="SlackIdempotencyKeyDerivationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using AgentSwarm.Messaging.Slack.Pipeline;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 4.3 contract pin for the architecture.md §3.4 idempotency
/// key derivation rules. Locks the three key shapes (event /
/// command / interaction) plus the rejected inputs so the helper
/// cannot silently drift from the architecture.
/// </summary>
public sealed class SlackIdempotencyKeyDerivationTests
{
    [Fact]
    public void ForEvent_uses_event_prefix_and_event_id()
    {
        string key = SlackIdempotencyKeyDerivation.ForEvent("Ev123");

        key.Should().Be("event:Ev123");
        SlackIdempotencyKeyDerivation.EventPrefix.Should().Be("event:");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForEvent_rejects_null_or_empty_event_id(string? eventId)
    {
        Action act = () => SlackIdempotencyKeyDerivation.ForEvent(eventId!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForCommand_concatenates_team_user_command_and_trigger_with_cmd_prefix()
    {
        string key = SlackIdempotencyKeyDerivation.ForCommand(
            teamId: "T0123ABCD",
            userId: "U999",
            command: "/agent",
            triggerId: "trig-789");

        key.Should().Be("cmd:T0123ABCD:U999:/agent:trig-789");
        SlackIdempotencyKeyDerivation.CommandPrefix.Should().Be("cmd:");
    }

    [Fact]
    public void ForInteraction_concatenates_team_user_actionOrView_and_trigger_with_interact_prefix()
    {
        string key = SlackIdempotencyKeyDerivation.ForInteraction(
            teamId: "T0123ABCD",
            userId: "U999",
            actionOrViewId: "view-42",
            triggerId: "trig-42");

        key.Should().Be("interact:T0123ABCD:U999:view-42:trig-42");
        SlackIdempotencyKeyDerivation.InteractionPrefix.Should().Be("interact:");
    }

    [Theory]
    [InlineData(null, "U", "/agent", "trig")]
    [InlineData("T", null, "/agent", "trig")]
    [InlineData("T", "U", null, "trig")]
    [InlineData("T", "U", "/agent", null)]
    [InlineData("", "U", "/agent", "trig")]
    public void ForCommand_rejects_any_missing_part(string? team, string? user, string? command, string? trigger)
    {
        Action act = () => SlackIdempotencyKeyDerivation.ForCommand(team!, user!, command!, trigger!);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null, "U", "view", "trig")]
    [InlineData("T", null, "view", "trig")]
    [InlineData("T", "U", null, "trig")]
    [InlineData("T", "U", "view", null)]
    public void ForInteraction_rejects_any_missing_part(string? team, string? user, string? actionOrView, string? trigger)
    {
        Action act = () => SlackIdempotencyKeyDerivation.ForInteraction(team!, user!, actionOrView!, trigger!);
        act.Should().Throw<ArgumentException>();
    }
}
